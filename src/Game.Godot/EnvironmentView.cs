using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Presentation environment view managing DirectionalLight3D, WorldEnvironment,
/// procedural sky, atmosphere haze, and graphics quality levels.
/// Pure presentation component with zero simulation dependencies and zero per-frame heap allocations.
/// </summary>
public sealed partial class EnvironmentView : Node3D
{
    private readonly DirectionalLight3D _sun;
    private readonly WorldEnvironment _worldEnvironment;
    private readonly ProceduralSkyMaterial _skyMaterial;
    private EnvironmentProfile _activeProfile = null!;
    private EnvironmentQuality _activeQuality = EnvironmentQuality.High;
    private float _lastCameraSize = -999f;
    private float _lastCameraPitch = -999f;
    private const float SizeUpdateThreshold = 1.0f;
    private const float PitchUpdateThreshold = 0.02f;

    public DirectionalLight3D Sun => _sun;
    public WorldEnvironment WorldEnvironment => _worldEnvironment;
    public Godot.Environment GodotEnvironment => _worldEnvironment.Environment;
    public EnvironmentProfile ActiveProfile => _activeProfile;
    public EnvironmentQuality ActiveQuality => _activeQuality;

    public EnvironmentView()
    {
        Name = "EnvironmentView";

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
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth
        };

        _worldEnvironment = new WorldEnvironment
        {
            Name = "WorldEnvironment",
            Environment = env
        };
        AddChild(_worldEnvironment);
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

        // 3. Ambient lighting
        string ambientSource = profile.Ambient.Source.Trim().ToLowerInvariant();
        env.AmbientLightSource = ambientSource switch
        {
            "color" => Godot.Environment.AmbientSource.Color,
            "disabled" => Godot.Environment.AmbientSource.Disabled,
            _ => Godot.Environment.AmbientSource.Sky
        };
        env.AmbientLightColor = new Color(profile.Ambient.ColorHex);
        env.AmbientLightEnergy = profile.Ambient.Energy;

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

        // 7. Glow
        if (profile.Glow is not null && profile.Glow.Enabled)
        {
            env.GlowIntensity = profile.Glow.Intensity;
            env.GlowBloom = profile.Glow.Bloom;
        }

        // Apply quality level adjustments
        SetQuality(quality);
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
                env.SsaoEnabled = false;
                env.GlowEnabled = false;
                env.FogAerialPerspective = 0f;
                break;

            case EnvironmentQuality.Medium:
                _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
                _sun.DirectionalShadowMaxDistance = 1600f;
                env.SsaoEnabled = _activeProfile.Ssao?.Enabled ?? false;
                env.GlowEnabled = _activeProfile.Glow?.Enabled ?? false;
                env.FogAerialPerspective = _activeProfile.Haze.AerialPerspective * 0.75f;
                break;

            case EnvironmentQuality.High:
            default:
                _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
                _sun.DirectionalShadowMaxDistance = _activeProfile.Sun.ShadowMaxDistance;
                env.SsaoEnabled = _activeProfile.Ssao?.Enabled ?? false;
                env.GlowEnabled = _activeProfile.Glow?.Enabled ?? false;
                env.FogAerialPerspective = _activeProfile.Haze.AerialPerspective;
                break;
        }
    }

    public static float CalculateHazeDensity(float cameraSize, float closeDensity, float wideDensity)
    {
        float t = Mathf.Clamp((cameraSize - 120f) / (5500f - 120f), 0f, 1f);
        return Mathf.Lerp(closeDensity, wideDensity, t);
    }

    public void UpdateHazeForCamera(float cameraSize, Vector3 cameraPosition, float cameraPitch = -1f)
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

        if (!_activeProfile.Haze.Enabled) return;

        if (MathF.Abs(cameraSize - _lastCameraSize) < SizeUpdateThreshold &&
            MathF.Abs(effectivePitch - _lastCameraPitch) < PitchUpdateThreshold)
        {
            return;
        }

        _lastCameraSize = cameraSize;
        _lastCameraPitch = effectivePitch;

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
