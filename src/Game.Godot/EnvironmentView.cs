using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

public enum ShadowZoomTier : byte
{
    Close = 0,
    Play = 1,
    Wide = 2
}

/// <summary>
/// Presentation environment view managing DirectionalLight3D, WorldEnvironment,
/// procedural sky, atmosphere haze, shadow zoom tiers, and graphics quality levels.
/// Pure presentation component with zero simulation dependencies and zero per-frame heap allocations.
/// </summary>
public sealed partial class EnvironmentView : Node3D
{
    private static Shader? s_rainShader;
    private static Texture2D? s_rainStreaksTex;
    private readonly DirectionalLight3D _sun;
    private readonly WorldEnvironment _worldEnvironment;
    private readonly ProceduralSkyMaterial _skyMaterial;
    private readonly CanvasLayer _rainLayer;
    private readonly ColorRect _rainRect;
    private readonly ShaderMaterial _rainMaterial;
    private EnvironmentProfile _activeProfile = null!;
    private EnvironmentQuality _activeQuality = EnvironmentQuality.High;
    private AmbientOcclusionPreset _ambientOcclusionPreset = AmbientOcclusionPreset.Auto;
    private Vector2I _outputPixelSize = new(1440, 900);
    private float _lastCameraSize = -999f;
    private float _lastCameraPitch = -999f;
    private float _activeRainOpacity;
    private Color _lastRainTint = new(-1f, -1f, -1f, -1f);
    private ShadowZoomTier? _activeShadowTier;
    private bool _shadowTierApplied;
    private bool _effectiveTreeShadows;
    private bool _effectiveTerrainShadows = true;
    private int _shadowTierApplyCount;
    private IMapShadowLayers? _lastShadowTarget;
    private RenderBudget _renderBudget;
    private BenchmarkRenderOverrides? _benchmarkOverrides;
    private float _shadowFadeOpacity = 1f;
    private float _targetShadowFadeOpacity = 1f;
    private const float ShadowFadeDurationSeconds = 0.3f;
    private const float SizeUpdateThreshold = 1.0f;
    private const float PitchUpdateThreshold = 0.02f;
    private const float RainFadeStartCameraSize = 380f;
    private const float RainMaxCameraSize = 680f;
    private const string RainShaderCode = @"
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D rain_tex : filter_linear_mipmap, repeat_enable;
uniform float opacity : hint_range(0.0, 1.0) = 0.0;
uniform vec3 rain_color : source_color = vec3(0.76, 0.82, 0.88);
uniform float speed = 1.55;
uniform float wind_x = 0.14;

void fragment() {
    vec2 uv1 = SCREEN_UV * vec2(5.2, 2.6) + vec2(-TIME * wind_x * 1.35, -TIME * speed * 1.30);
    vec2 uv2 = SCREEN_UV * vec2(7.6, 3.8) + vec2(0.37 - TIME * wind_x * 0.85, 0.61 - TIME * speed * 0.88);
    float a1 = texture(rain_tex, uv1).a;
    float a2 = texture(rain_tex, uv2).a * 0.62;
    float alpha = clamp((a1 + a2) * opacity, 0.0, 0.32);
    COLOR = vec4(rain_color, alpha);
}
";

    public DirectionalLight3D Sun => _sun;
    public WorldEnvironment WorldEnvironment => _worldEnvironment;
    public Godot.Environment GodotEnvironment => _worldEnvironment.Environment;
    public EnvironmentProfile ActiveProfile => _activeProfile;
    public EnvironmentQuality ActiveQuality => _activeQuality;
    public RenderBudget ActiveRenderBudget => _renderBudget;
    public ShadowZoomTier? ActiveShadowTier => _activeShadowTier;
    public bool EffectiveTreeShadows => _effectiveTreeShadows;
    public bool EffectiveTerrainShadows => _effectiveTerrainShadows;
    public int ShadowTierApplyCount => _shadowTierApplyCount;
    public bool IsRainActive => _rainLayer.Visible && _rainRect.Visible && _activeRainOpacity > 0.01f;
    internal CanvasLayer RainLayer => _rainLayer;
    internal float ActiveRainOpacity => _activeRainOpacity;
    public float ActiveWetness => _activeProfile?.Weather?.Wetness ?? 0f;

    public EnvironmentView(RenderBudget? renderBudget = null)
    {
        Name = "EnvironmentView";
        _renderBudget = renderBudget ?? RenderBudget.LoadDefault();

        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits
        };
        AddChild(_sun);

        _skyMaterial = new ProceduralSkyMaterial();
        Godot.Environment env = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("17211c"),
            Sky = new Sky
            {
                SkyMaterial = _skyMaterial
            },
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth
        };

        _worldEnvironment = new WorldEnvironment
        {
            Name = "WorldEnvironment",
            Environment = env
        };
        AddChild(_worldEnvironment);

        s_rainShader ??= new Shader { Code = RainShaderCode };
        s_rainStreaksTex ??= GD.Load<Texture2D>("res://Assets/Terrain/rain_streaks.png");
        _rainMaterial = new ShaderMaterial { Shader = s_rainShader };
        if (s_rainStreaksTex is not null)
        {
            _rainMaterial.SetShaderParameter("rain_tex", s_rainStreaksTex);
        }
        _rainMaterial.SetShaderParameter("opacity", 0f);

        _rainLayer = new CanvasLayer
        {
            Name = "WeatherRainLayer",
            Layer = -10,
            Visible = false
        };
        _rainRect = new ColorRect
        {
            Name = "RainOverlay",
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 0f,
            OffsetTop = 0f,
            OffsetRight = 0f,
            OffsetBottom = 0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = Colors.White,
            Material = _rainMaterial
        };
        _rainLayer.AddChild(_rainRect);
        AddChild(_rainLayer);
    }

    public void ApplyProfile(EnvironmentProfile profile, EnvironmentQuality quality = EnvironmentQuality.High)
    {
        _activeProfile = profile;
        _activeQuality = quality;
        _lastCameraSize = -999f;
        _lastCameraPitch = -999f;

        // 1. Sun configuration
        _sun.RotationDegrees = new Vector3(profile.Sun.Pitch, profile.Sun.Yaw, 0f);
        _sun.LightColor = new Color(profile.Sun.ColorHex);
        _sun.LightEnergy = profile.Sun.Energy;
        _sun.ShadowEnabled = profile.Sun.ShadowEnabled;
        _sun.DirectionalShadowPancakeSize = profile.Sun.ShadowPancakeSize;
        _sun.ShadowBias = profile.Sun.ShadowBias;
        _sun.ShadowBlur = profile.Sun.ShadowBlur;

        // 2. Sky configuration
        _skyMaterial.SkyTopColor = new Color(profile.Sky.SkyTopColorHex);
        _skyMaterial.SkyHorizonColor = new Color(profile.Sky.SkyHorizonColorHex);
        _skyMaterial.GroundBottomColor = new Color(profile.Sky.GroundBottomColorHex);
        _skyMaterial.GroundHorizonColor = new Color(profile.Sky.GroundHorizonColorHex);
        _skyMaterial.SunAngleMax = profile.Sky.SunAngleMax;
        _skyMaterial.EnergyMultiplier = profile.Sky.EnergyMultiplier;

        Godot.Environment env = _worldEnvironment.Environment;
        env.BackgroundColor = new Color(profile.Haze.ColorHex);
        env.BackgroundMode = Godot.Environment.BGMode.Color;

        // 3. Ambient lighting & Sky IBL reflections
        string ambientSource = profile.Ambient.Source.Trim().ToLowerInvariant();
        env.AmbientLightSource = ambientSource switch
        {
            "color" => Godot.Environment.AmbientSource.Color,
            "disabled" => Godot.Environment.AmbientSource.Disabled,
            _ => Godot.Environment.AmbientSource.Sky
        };
        env.AmbientLightColor = new Color(profile.Ambient.ColorHex);
        env.AmbientLightEnergy = profile.Ambient.Energy;
        env.AmbientLightSkyContribution = env.AmbientLightSource == Godot.Environment.AmbientSource.Sky ? 0.55f : 0f;
        env.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;

        // 4. Tonemap and exposure
        string tonemap = profile.Tonemap.Mode.Trim().ToLowerInvariant();
        env.TonemapMode = tonemap switch
        {
            "linear" => Godot.Environment.ToneMapper.Linear,
            "reinhard" or "reinhardt" => Godot.Environment.ToneMapper.Reinhardt,
            "aces" => Godot.Environment.ToneMapper.Aces,
            _ => Godot.Environment.ToneMapper.Filmic
        };
        env.TonemapExposure = profile.Tonemap.Exposure;
        env.TonemapWhite = profile.Tonemap.White;

        // 5. Haze / Atmospheric Fog
        env.FogEnabled = profile.Haze.Enabled;
        string hazeMode = profile.Haze.Mode?.Trim().ToLowerInvariant() ?? "exponential";
        env.FogMode = hazeMode == "depth"
            ? Godot.Environment.FogModeEnum.Depth
            : Godot.Environment.FogModeEnum.Exponential;
        env.FogLightColor = new Color(profile.Haze.ColorHex);
        env.FogLightEnergy = profile.Haze.Energy;
        env.FogDensity = profile.Haze.Density;
        env.FogHeight = profile.Haze.Height;
        env.FogHeightDensity = profile.Haze.HeightDensity;
        env.FogDepthBegin = profile.Haze.DepthBegin;
        env.FogDepthEnd = profile.Haze.DepthEnd;

        // 6. SSAO
        if (profile.Ssao is not null && profile.Ssao.Enabled)
        {
            env.SsaoRadius = profile.Ssao.Radius;
            env.SsaoIntensity = profile.Ssao.Intensity;
            env.SsaoPower = profile.Ssao.Power;
            env.SsaoDetail = profile.Ssao.Detail;
        }

        // 7. Glow (restricted strictly to HDR > 1.0 combat VFX via GlowHdrThreshold)
        if (profile.Glow is not null && profile.Glow.Enabled)
        {
            env.GlowIntensity = profile.Glow.Intensity;
            env.GlowBloom = profile.Glow.Bloom;
            env.GlowHdrThreshold = profile.Glow.HdrThreshold;
            env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        }

        // Apply quality level adjustments
        _shadowTierApplied = false;
        SetQuality(quality);
    }

    public static float CalculateRainZoomFade(float cameraSize)
    {
        if (cameraSize <= 0f || cameraSize <= RainFadeStartCameraSize) return 1.0f;
        if (cameraSize >= RainMaxCameraSize) return 0.0f;
        return (RainMaxCameraSize - cameraSize) / (RainMaxCameraSize - RainFadeStartCameraSize);
    }

    public void SetProfile(EnvironmentProfile profile)
    {
        ApplyProfile(profile, _activeQuality);
    }

    public void SetQuality(EnvironmentQuality quality)
    {
        _activeQuality = quality;
        if (_activeProfile is null) return;

        Godot.Environment env = _worldEnvironment.Environment;

        switch (quality)
        {
            case EnvironmentQuality.Low:
                _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
                _sun.DirectionalShadowMaxDistance = 1200f;
                env.GlowEnabled = false;
                env.FogAerialPerspective = 0f;
                break;

            case EnvironmentQuality.Medium:
                _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
                _sun.DirectionalShadowMaxDistance = 1600f;
                env.GlowEnabled = _activeProfile.Glow?.Enabled ?? false;
                env.FogAerialPerspective = _activeProfile.Haze.AerialPerspective * 0.75f;
                break;

            case EnvironmentQuality.High:
            default:
                _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
                _sun.DirectionalShadowMaxDistance = _activeProfile.Sun.ShadowMaxDistance;
                env.GlowEnabled = _activeProfile.Glow?.Enabled ?? false;
                env.FogAerialPerspective = _activeProfile.Haze.AerialPerspective;
                break;
        }

        env.SsaoEnabled = DisplaySettings.ResolveSsaoEnabled(
            _ambientOcclusionPreset,
            quality,
            _activeProfile.Ssao?.Enabled ?? false,
            _outputPixelSize.X,
            _outputPixelSize.Y);

        _shadowTierApplied = false;
        if (_activeShadowTier.HasValue)
        {
            ApplyShadowTier(_activeShadowTier.Value, GetAliveTarget(_lastShadowTarget), allowFade: false);
        }
        ApplyBenchmarkOverridesToEngine();
        UpdateRainState(_lastCameraSize > 0f ? _lastCameraSize : 0f);
    }

    private void UpdateRainState(float cameraSize)
    {
        float intensity = _activeProfile?.Weather?.RainIntensity ?? 0f;
        float zoomFade = CalculateRainZoomFade(cameraSize);
        bool enabled = intensity > 0.01f && _activeQuality != EnvironmentQuality.Low && zoomFade > 0.01f;
        float qualityScale = _activeQuality == EnvironmentQuality.Medium ? 0.72f : 1.0f;
        float targetOpacity = enabled ? Mathf.Clamp(intensity * zoomFade * qualityScale * 0.22f, 0f, 0.30f) : 0f;

        if (_rainLayer.Visible != enabled)
        {
            _rainLayer.Visible = enabled;
        }

        if (MathF.Abs(targetOpacity - _activeRainOpacity) > 0.004f || (!enabled && _activeRainOpacity > 0f))
        {
            _activeRainOpacity = targetOpacity;
            _rainMaterial.SetShaderParameter("opacity", targetOpacity);
        }

        if (enabled && _activeProfile is not null)
        {
            Color tint = new Color(_activeProfile.Haze.ColorHex).Lerp(new Color(0.80f, 0.85f, 0.90f), 0.55f);
            if (MathF.Abs(tint.R - _lastRainTint.R) > 0.005f ||
                MathF.Abs(tint.G - _lastRainTint.G) > 0.005f ||
                MathF.Abs(tint.B - _lastRainTint.B) > 0.005f)
            {
                _lastRainTint = tint;
                _rainMaterial.SetShaderParameter("rain_color", new Vector3(tint.R, tint.G, tint.B));
            }
        }
    }

    public void SetDisplayOutputAndSsao(AmbientOcclusionPreset preset, Vector2I outputPixelSize)
    {
        _ambientOcclusionPreset = preset;
        if (outputPixelSize is { X: > 0, Y: > 0 })
        {
            _outputPixelSize = outputPixelSize;
        }
        if (_activeProfile is not null)
        {
            _worldEnvironment.Environment.SsaoEnabled = DisplaySettings.ResolveSsaoEnabled(
                _ambientOcclusionPreset,
                _activeQuality,
                _activeProfile.Ssao?.Enabled ?? false,
                _outputPixelSize.X,
                _outputPixelSize.Y);
        }
        ApplyBenchmarkOverridesToEngine();
    }

    public void ApplyBenchmarkOverrides(BenchmarkRenderOverrides? overrides)
    {
        _benchmarkOverrides = overrides;
        if (_activeProfile is not null)
        {
            SetQuality(_activeQuality);
        }
        else
        {
            ApplyBenchmarkOverridesToEngine();
        }
    }

    private void ApplyBenchmarkOverridesToEngine()
    {
        BenchmarkRenderOverrides? cli = _benchmarkOverrides;
        if (cli is null) return;

        Godot.Environment env = _worldEnvironment.Environment;
        if (cli.SsaoMode is not null)
        {
            switch (cli.SsaoMode)
            {
                case "off":
                    env.SsaoEnabled = false;
                    break;
                case "half":
                    env.SsaoEnabled = true;
                    RenderingServer.EnvironmentSetSsaoQuality(RenderingServer.EnvironmentSsaoQuality.Medium, true, 0.5f, 2, 50f, 300f);
                    break;
                case "full":
                    env.SsaoEnabled = true;
                    RenderingServer.EnvironmentSetSsaoQuality(RenderingServer.EnvironmentSsaoQuality.Medium, false, 0.5f, 2, 50f, 300f);
                    break;
            }
        }

        if (cli.GlowEnabled.HasValue)
        {
            env.GlowEnabled = cli.GlowEnabled.Value;
        }

        if (cli.ShadowSize.HasValue)
        {
            RenderingServer.DirectionalShadowAtlasSetSize(cli.ShadowSize.Value, true);
        }

        if (cli.ShadowFilter is not null)
        {
            RenderingServer.ShadowQuality q = cli.ShadowFilter switch
            {
                "hard" => RenderingServer.ShadowQuality.Hard,
                "very_low" => RenderingServer.ShadowQuality.SoftVeryLow,
                "low" => RenderingServer.ShadowQuality.SoftLow,
                "high" => RenderingServer.ShadowQuality.SoftHigh,
                _ => RenderingServer.ShadowQuality.SoftMedium
            };
            RenderingServer.DirectionalSoftShadowFilterSetQuality(q);
        }
    }

    public void SetRenderBudget(RenderBudget renderBudget)
    {
        _renderBudget = renderBudget ?? throw new ArgumentNullException(nameof(renderBudget));
        _shadowTierApplied = false;
        if (_activeShadowTier.HasValue)
        {
            ApplyShadowTier(_activeShadowTier.Value, GetAliveTarget(_lastShadowTarget), allowFade: false);
        }
        ApplyBenchmarkOverridesToEngine();
    }

    public void SetShadowTarget(IMapShadowLayers? mapLayers)
    {
        IMapShadowLayers? alive = GetAliveTarget(mapLayers);
        if (!ReferenceEquals(alive, _lastShadowTarget))
        {
            _lastShadowTarget = alive;
            _shadowTierApplied = false;
        }
    }

    private static IMapShadowLayers? GetAliveTarget(IMapShadowLayers? target)
    {
        if (target is null) return null;
        if (target is Node node)
        {
            return GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion() ? target : null;
        }
        if (target is GodotObject go)
        {
            return GodotObject.IsInstanceValid(go) ? target : null;
        }
        return target;
    }

    public static ShadowZoomTier SelectShadowZoomTier(
        float cameraSize,
        ShadowZoomTier? previousTier,
        ShadowTiersSettings? settings = null)
    {
        ShadowTiersSettings s = settings ?? RenderBudget.Default.ShadowTiers;
        if (!previousTier.HasValue)
        {
            if (cameraSize < s.CloseToPlay) return ShadowZoomTier.Close;
            if (cameraSize < s.PlayToWide) return ShadowZoomTier.Play;
            return ShadowZoomTier.Wide;
        }

        float h = s.Hysteresis;
        return previousTier.Value switch
        {
            ShadowZoomTier.Close => cameraSize > s.CloseToPlay + h
                ? (cameraSize > s.PlayToWide + h ? ShadowZoomTier.Wide : ShadowZoomTier.Play)
                : ShadowZoomTier.Close,
            ShadowZoomTier.Play => cameraSize < s.CloseToPlay - h
                ? ShadowZoomTier.Close
                : (cameraSize > s.PlayToWide + h ? ShadowZoomTier.Wide : ShadowZoomTier.Play),
            ShadowZoomTier.Wide => cameraSize < s.PlayToWide - h
                ? (cameraSize < s.CloseToPlay - h ? ShadowZoomTier.Close : ShadowZoomTier.Play)
                : ShadowZoomTier.Wide,
            _ => ShadowZoomTier.Play
        };
    }

    public static (bool ShadowEnabled, DirectionalLight3D.ShadowMode ShadowMode, float ShadowMaxDistance, bool TreeShadows, bool TerrainShadows)
        ResolveTierShadowSettings(ShadowTiersSettings tiers, ShadowZoomTier tier, EnvironmentQuality quality, SunSettings sun)
    {
        ShadowTierSettings cfg = tier switch
        {
            ShadowZoomTier.Close => tiers.Close,
            ShadowZoomTier.Wide => tiers.Wide,
            _ => tiers.Play
        };

        bool enabled = sun.ShadowEnabled && cfg.ShadowEnabled;
        int maxSplits = quality switch
        {
            EnvironmentQuality.Low => 1,
            EnvironmentQuality.Medium => 2,
            _ => 4
        };
        int effectiveSplits = Math.Min(cfg.ShadowSplits, maxSplits);
        DirectionalLight3D.ShadowMode mode = effectiveSplits switch
        {
            >= 4 => DirectionalLight3D.ShadowMode.Parallel4Splits,
            >= 2 => DirectionalLight3D.ShadowMode.Parallel2Splits,
            _ => DirectionalLight3D.ShadowMode.Orthogonal
        };

        float qualityMaxDist = quality switch
        {
            EnvironmentQuality.Low => 1200f,
            EnvironmentQuality.Medium => 1600f,
            _ => sun.ShadowMaxDistance
        };
        float maxDist = Math.Min(cfg.ShadowMaxDistance, qualityMaxDist);
        bool treeShadows = enabled && cfg.TreeShadows && quality != EnvironmentQuality.Low;
        bool terrainShadows = enabled && cfg.TerrainShadows;
        return (enabled, mode, maxDist, treeShadows, terrainShadows);
    }

    public bool UpdateShadowTierForCamera(float cameraSize, IMapShadowLayers? mapLayers = null)
    {
        if (_activeProfile is null) return false;

        bool targetChanged = false;
        if (mapLayers is not null)
        {
            IMapShadowLayers? aliveTarget = GetAliveTarget(mapLayers);
            if (!ReferenceEquals(aliveTarget, _lastShadowTarget))
            {
                _lastShadowTarget = aliveTarget;
                _shadowTierApplied = false;
                targetChanged = true;
            }
        }
        else
        {
            IMapShadowLayers? aliveExisting = GetAliveTarget(_lastShadowTarget);
            if (!ReferenceEquals(aliveExisting, _lastShadowTarget))
            {
                _lastShadowTarget = null;
                targetChanged = true;
            }
        }

        ShadowZoomTier nextTier = SelectShadowZoomTier(cameraSize, _activeShadowTier, _renderBudget.ShadowTiers);
        if (_shadowTierApplied && _activeShadowTier == nextTier)
        {
            return false;
        }

        bool isTransition = !targetChanged && _activeShadowTier.HasValue && _activeShadowTier.Value != nextTier;
        ApplyShadowTier(nextTier, _lastShadowTarget, allowFade: isTransition);
        return true;
    }

    public void UpdateShadowFade(double delta)
    {
        if (MathF.Abs(_shadowFadeOpacity - _targetShadowFadeOpacity) <= 0.0001f)
        {
            return;
        }

        float step = (float)(Math.Max(0.0, delta) / ShadowFadeDurationSeconds);
        if (_shadowFadeOpacity < _targetShadowFadeOpacity)
        {
            _shadowFadeOpacity = MathF.Min(_targetShadowFadeOpacity, _shadowFadeOpacity + step);
        }
        else
        {
            _shadowFadeOpacity = MathF.Max(_targetShadowFadeOpacity, _shadowFadeOpacity - step);
        }

        _sun.ShadowOpacity = _shadowFadeOpacity;
        if (_targetShadowFadeOpacity <= 0f && _shadowFadeOpacity <= 0.001f)
        {
            _sun.ShadowEnabled = false;
            IMapShadowLayers? target = GetAliveTarget(_lastShadowTarget);
            target?.SetVegetationShadowsEnabled(_effectiveTreeShadows);
            target?.SetTerrainShadowsEnabled(_effectiveTerrainShadows);
        }
    }

    private void ApplyShadowTier(ShadowZoomTier tier, IMapShadowLayers? mapLayers, bool allowFade)
    {
        (bool enabled, DirectionalLight3D.ShadowMode mode, float maxDist, bool treeShadows, bool terrainShadows) =
            ResolveTierShadowSettings(_renderBudget.ShadowTiers, tier, _activeQuality, _activeProfile.Sun);

        float nextTargetOpacity = enabled ? 1.0f : 0.0f;
        bool fadingOut = allowFade && nextTargetOpacity <= 0f && _shadowFadeOpacity > 0.01f;
        if (allowFade && MathF.Abs(nextTargetOpacity - _shadowFadeOpacity) > 0.01f)
        {
            _targetShadowFadeOpacity = nextTargetOpacity;
            _sun.ShadowEnabled = true;
            _sun.ShadowOpacity = _shadowFadeOpacity;
            if (enabled)
            {
                _sun.DirectionalShadowMode = mode;
                _sun.DirectionalShadowMaxDistance = maxDist;
            }
        }
        else
        {
            _targetShadowFadeOpacity = nextTargetOpacity;
            _shadowFadeOpacity = nextTargetOpacity;
            _sun.ShadowOpacity = nextTargetOpacity;
            _sun.ShadowEnabled = enabled;
            _sun.DirectionalShadowMode = mode;
            _sun.DirectionalShadowMaxDistance = maxDist;
        }

        _effectiveTreeShadows = treeShadows;
        _effectiveTerrainShadows = terrainShadows;
        _activeShadowTier = tier;
        _shadowTierApplied = true;
        _shadowTierApplyCount++;
        IMapShadowLayers? target = GetAliveTarget(mapLayers);
        if (target is not null && !fadingOut)
        {
            target.SetVegetationShadowsEnabled(treeShadows);
            target.SetTerrainShadowsEnabled(terrainShadows);
        }
    }

    public static float CalculateHazeDensity(float cameraSize, float closeDensity, float wideDensity)
    {
        float t = Mathf.Clamp((cameraSize - 120f) / (5500f - 120f), 0f, 1f);
        return Mathf.Lerp(closeDensity, wideDensity, t);
    }

    public void UpdateHazeForCamera(float cameraSize, Vector3 cameraPosition, float cameraPitch = -1f, float cameraYaw = 0f)
    {
        if (_activeProfile is null) return;

        float effectivePitch = cameraPitch >= 0f ? cameraPitch : OrbitCameraPose.DefaultPitch;
        Godot.Environment env = _worldEnvironment.Environment;

        // When looking horizontally in perspective (low pitch < ~20 deg), show 3D procedural sky.
        // In top-down / RTS pitch (>= 20 deg / ~0.35 rad) or orthogonal projection, use uniform
        // atmospheric haze color matching FogLightColor to prevent the fake horizontal horizon artifact.
        if (effectivePitch < 0.35f)
        {
            if (env.BackgroundMode != Godot.Environment.BGMode.Sky)
            {
                env.BackgroundMode = Godot.Environment.BGMode.Sky;
            }
        }
        else
        {
            if (env.BackgroundMode != Godot.Environment.BGMode.Color)
            {
                env.BackgroundMode = Godot.Environment.BGMode.Color;
                env.BackgroundColor = new Color(_activeProfile.Haze.ColorHex);
            }
        }

        if (MathF.Abs(cameraSize - _lastCameraSize) < SizeUpdateThreshold &&
            MathF.Abs(effectivePitch - _lastCameraPitch) < PitchUpdateThreshold)
        {
            return;
        }

        _lastCameraSize = cameraSize;
        _lastCameraPitch = effectivePitch;

        UpdateRainState(cameraSize);

        if (!_activeProfile.Haze.Enabled) return;

        float density = CalculateHazeDensity(cameraSize, _activeProfile.Haze.CloseZoomDensity, _activeProfile.Haze.WideZoomDensity);
        env.FogDensity = density;

        if (env.FogMode == Godot.Environment.FogModeEnum.Depth)
        {
            float t = Mathf.Clamp((cameraSize - 120f) / (5500f - 120f), 0f, 1f);
            env.FogDepthBegin = Mathf.Lerp(_activeProfile.Haze.DepthBegin, _activeProfile.Haze.DepthBegin * 10f, t);
            env.FogDepthEnd = Mathf.Lerp(_activeProfile.Haze.DepthEnd, _activeProfile.Haze.DepthEnd * 2.5f, t);
        }
    }
}
