using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// In-game settings overlay for VSync, frame pacing limit, environment profile,
/// and graphics quality configuration.
/// Persists player choices to user://display_settings.cfg with safe default fallback.
/// </summary>
internal sealed partial class SettingsPanel : CanvasLayer
{
    public static readonly bool IsHeadlessDisplay = DisplayServer.GetName() == "headless";
    public static BenchmarkRenderOverrides? ActiveBenchmarkOverrides { get; set; }

    internal const string ConfigPath = "user://display_settings.cfg";
    private readonly Control _backdrop;
    private readonly PanelContainer _container;
    private readonly CheckButton _vsyncButton;
    private readonly OptionButton _fpsOptions;
    private readonly OptionButton _profileOptions;
    private readonly OptionButton _qualityOptions;
    private readonly OptionButton _renderScaleOptions;
    private readonly OptionButton _antiAliasingOptions;
    private readonly OptionButton _ssaoOptions;
    private readonly Label _statusLabel;
    private DisplaySettings _savedConfig = DisplaySettings.CreateDefault();
    private DisplaySettings _effectiveSettings = DisplaySettings.CreateDefault();
    private bool _cliOverridesVsync;
    private bool _cliOverridesMaxFps;
    private bool _cliOverridesProfile;
    private bool _cliOverridesQuality;
    private bool _cliOverridesRenderScale;
    private bool _cliOverridesAntiAliasing;
    private bool _cliOverridesSsao;

    public event Action<DisplaySettings>? SettingsChanged;

    public DisplaySettings CurrentSettings => _effectiveSettings;
    public DisplaySettings SavedConfig => _savedConfig;
    public bool IsOpen => _container.Visible;
    public Rect2 GetContainerRect() => _container.Visible ? _container.GetGlobalRect() : default;

    public SettingsPanel()
    {
        Layer = 10;

        _backdrop = HudTheme.CreateModalBackdrop(visible: false);
        AddChild(_backdrop);

        _container = new PanelContainer
        {
            Theme = HudTheme.Shared,
            Visible = false,
            CustomMinimumSize = new Vector2(420, 420)
        };
        HudTheme.CenterModal(_container);
        _container.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(accented: true, padding: 2, topRail: true, modal: true));
        AddChild(_container);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        _container.AddChild(margin);

        VBoxContainer stack = new();
        stack.AddThemeConstantOverride("separation", 14);
        margin.AddChild(stack);

        Label title = new()
        {
            Text = "DISPLAY & ENVIRONMENT SETTINGS",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", HudTheme.Accent);
        stack.AddChild(title);

        // Environment Profile Row
        HBoxContainer profileRow = new();
        profileRow.AddThemeConstantOverride("separation", 12);
        Label profileLabel = new() { Text = "Environment:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        profileLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _profileOptions = new OptionButton();
        _profileOptions.AddItem("Overcast Autumn (Ustye)", 0);
        _profileOptions.AddItem("Clear Afternoon", 1);
        _profileOptions.ItemSelected += OnProfileSelected;
        profileRow.AddChild(profileLabel);
        profileRow.AddChild(_profileOptions);
        stack.AddChild(profileRow);

        // Graphics Quality Row
        HBoxContainer qualityRow = new();
        qualityRow.AddThemeConstantOverride("separation", 12);
        Label qualityLabel = new() { Text = "Graphics Quality:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        qualityLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _qualityOptions = new OptionButton();
        _qualityOptions.AddItem("Low", 0);
        _qualityOptions.AddItem("Medium", 1);
        _qualityOptions.AddItem("High", 2);
        _qualityOptions.ItemSelected += OnQualitySelected;
        qualityRow.AddChild(qualityLabel);
        qualityRow.AddChild(_qualityOptions);
        stack.AddChild(qualityRow);

        // Render Scale Row
        HBoxContainer renderScaleRow = new();
        renderScaleRow.AddThemeConstantOverride("separation", 12);
        Label renderScaleLabel = new() { Text = "3D Render Scale:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        renderScaleLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _renderScaleOptions = new OptionButton();
        _renderScaleOptions.AddItem("Auto (Pixel Budget)", 0);
        _renderScaleOptions.AddItem("100% (Native)", 1);
        _renderScaleOptions.AddItem("85% (FSR)", 2);
        _renderScaleOptions.AddItem("75% (FSR)", 3);
        _renderScaleOptions.AddItem("50% (FSR)", 4);
        _renderScaleOptions.ItemSelected += OnRenderScaleSelected;
        renderScaleRow.AddChild(renderScaleLabel);
        renderScaleRow.AddChild(_renderScaleOptions);
        stack.AddChild(renderScaleRow);

        // Anti-aliasing Row
        HBoxContainer aaRow = new();
        aaRow.AddThemeConstantOverride("separation", 12);
        Label aaLabel = new() { Text = "Anti-aliasing:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        aaLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _antiAliasingOptions = new OptionButton();
        _antiAliasingOptions.AddItem("Auto (MSAA + FXAA)", 0);
        _antiAliasingOptions.AddItem("Off", 1);
        _antiAliasingOptions.AddItem("FXAA", 2);
        _antiAliasingOptions.AddItem("2x MSAA", 3);
        _antiAliasingOptions.AddItem("4x MSAA", 4);
        _antiAliasingOptions.ItemSelected += OnAntiAliasingSelected;
        aaRow.AddChild(aaLabel);
        aaRow.AddChild(_antiAliasingOptions);
        stack.AddChild(aaRow);

        // Ambient Occlusion (SSAO) Row
        HBoxContainer ssaoRow = new();
        ssaoRow.AddThemeConstantOverride("separation", 12);
        Label ssaoLabel = new() { Text = "Ambient Occlusion (SSAO):", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ssaoLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _ssaoOptions = new OptionButton();
        _ssaoOptions.AddItem("Auto (Off on HiDPI)", 0);
        _ssaoOptions.AddItem("Off", 1);
        _ssaoOptions.AddItem("On", 2);
        _ssaoOptions.ItemSelected += OnAmbientOcclusionSelected;
        ssaoRow.AddChild(ssaoLabel);
        ssaoRow.AddChild(_ssaoOptions);
        stack.AddChild(ssaoRow);

        // VSync Row
        HBoxContainer vsyncRow = new();
        vsyncRow.AddThemeConstantOverride("separation", 12);
        Label vsyncLabel = new() { Text = "Vertical Sync (VSync):", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vsyncLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _vsyncButton = new CheckButton { Text = "Enabled" };
        _vsyncButton.Toggled += OnVsyncToggled;
        vsyncRow.AddChild(vsyncLabel);
        vsyncRow.AddChild(_vsyncButton);
        stack.AddChild(vsyncRow);

        // FPS Limit Row
        HBoxContainer fpsRow = new();
        fpsRow.AddThemeConstantOverride("separation", 12);
        Label fpsLabel = new() { Text = "Frame Rate Cap:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fpsLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _fpsOptions = new OptionButton();
        _fpsOptions.AddItem("Unlimited (0)", 0);
        _fpsOptions.AddItem("30 FPS", 1);
        _fpsOptions.AddItem("60 FPS", 2);
        _fpsOptions.AddItem("120 FPS", 3);
        _fpsOptions.AddItem("144 FPS", 4);
        _fpsOptions.AddItem("165 FPS", 5);
        _fpsOptions.AddItem("240 FPS", 6);
        _fpsOptions.ItemSelected += OnFpsSelected;
        fpsRow.AddChild(fpsLabel);
        fpsRow.AddChild(_fpsOptions);
        stack.AddChild(fpsRow);

        _statusLabel = new Label
        {
            Text = "Settings saved to user config",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        stack.AddChild(_statusLabel);

        // Action Buttons Row
        HBoxContainer buttonRow = new();
        buttonRow.AddThemeConstantOverride("separation", 12);
        Button closeBtn = new() { Text = "Close [ESC]", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        closeBtn.Pressed += Close;
        buttonRow.AddChild(closeBtn);
        stack.AddChild(buttonRow);
    }

    public void Initialize(
        DisplaySettings savedConfig,
        DisplaySettings effectiveSettings,
        bool cliOverridesVsync,
        bool cliOverridesMaxFps,
        bool cliOverridesProfile = false,
        bool cliOverridesQuality = false,
        bool cliOverridesRenderScale = false,
        bool cliOverridesAntiAliasing = false,
        bool cliOverridesSsao = false)
    {
        _savedConfig = savedConfig.WithSanitizedValues();
        _effectiveSettings = effectiveSettings.WithSanitizedValues();
        _cliOverridesVsync = cliOverridesVsync;
        _cliOverridesMaxFps = cliOverridesMaxFps;
        _cliOverridesProfile = cliOverridesProfile;
        _cliOverridesQuality = cliOverridesQuality;
        _cliOverridesRenderScale = cliOverridesRenderScale;
        _cliOverridesAntiAliasing = cliOverridesAntiAliasing;
        _cliOverridesSsao = cliOverridesSsao;

        ApplyToEngine(_effectiveSettings);
        UpdateUiFromSettings();

        if (cliOverridesVsync || cliOverridesMaxFps || cliOverridesProfile || cliOverridesQuality || cliOverridesRenderScale || cliOverridesAntiAliasing || cliOverridesSsao)
        {
            _statusLabel.Text = "Some settings are overridden via command-line arguments";
            _statusLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        }
    }

    public void Initialize(DisplaySettings initialSettings, bool wasOverriddenByCli = false) =>
        Initialize(initialSettings, initialSettings, wasOverriddenByCli, wasOverriddenByCli, wasOverriddenByCli, wasOverriddenByCli, wasOverriddenByCli, wasOverriddenByCli, wasOverriddenByCli);

    public void Open()
    {
        UpdateUiFromSettings();
        _backdrop.Visible = true;
        _container.Visible = true;
        HudTheme.CenterModal(_container);
    }

    public void Close()
    {
        _backdrop.Visible = false;
        _container.Visible = false;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    private void UpdateUiFromSettings()
    {
        _vsyncButton.SetPressedNoSignal(_effectiveSettings.VSyncEnabled);
        _vsyncButton.Text = _effectiveSettings.VSyncEnabled ? "Enabled" : "Disabled";

        int idx = _effectiveSettings.MaxFps switch
        {
            30 => 1,
            60 => 2,
            120 => 3,
            144 => 4,
            165 => 5,
            240 => 6,
            _ => 0
        };
        _fpsOptions.Selected = idx;

        _profileOptions.Selected = _effectiveSettings.EnvironmentProfile == "clear_afternoon" ? 1 : 0;
        _qualityOptions.Selected = (int)_effectiveSettings.Quality;
        _renderScaleOptions.Selected = (int)_effectiveSettings.RenderScale;
        _antiAliasingOptions.Selected = _effectiveSettings.AntiAliasing switch
        {
            AntiAliasingPreset.Auto => 0,
            AntiAliasingPreset.Off => 1,
            AntiAliasingPreset.Fxaa => 2,
            AntiAliasingPreset.Msaa2x => 3,
            AntiAliasingPreset.Msaa4x => 4,
            _ => 0
        };
        _ssaoOptions.Selected = (int)_effectiveSettings.AmbientOcclusion;
    }

    private void OnProfileSelected(long index)
    {
        string profile = index == 1 ? "clear_afternoon" : "overcast";
        _cliOverridesProfile = false;
        CommitSettingEdit(
            _savedConfig with { EnvironmentProfile = profile },
            _effectiveSettings with { EnvironmentProfile = profile },
            "Environment profile applied and saved");
    }

    private void OnQualitySelected(long index)
    {
        EnvironmentQuality quality = index switch
        {
            0 => EnvironmentQuality.Low,
            1 => EnvironmentQuality.Medium,
            _ => EnvironmentQuality.High
        };
        _cliOverridesQuality = false;
        CommitSettingEdit(
            _savedConfig with { Quality = quality },
            _effectiveSettings with { Quality = quality },
            "Graphics quality applied and saved");
    }

    internal void SelectRenderScaleOptionForTest(int index) => OnRenderScaleSelected(index);
    internal void SelectAntiAliasingOptionForTest(int index) => OnAntiAliasingSelected(index);
    internal void SelectAmbientOcclusionOptionForTest(int index) => OnAmbientOcclusionSelected(index);

    private void OnRenderScaleSelected(long index)
    {
        RenderScalePreset scale = index switch
        {
            1 => RenderScalePreset.Native100,
            2 => RenderScalePreset.Scale85,
            3 => RenderScalePreset.Scale75,
            4 => RenderScalePreset.Scale50,
            _ => RenderScalePreset.Auto
        };
        _cliOverridesRenderScale = false;
        if (ActiveBenchmarkOverrides is { RenderScaleOverride: not null } activeCli)
        {
            ActiveBenchmarkOverrides = activeCli with { RenderScaleOverride = null };
        }
        CommitSettingEdit(
            _savedConfig with { RenderScale = scale },
            _effectiveSettings with { RenderScale = scale },
            "Render scale applied and saved");
    }

    private void OnAntiAliasingSelected(long index)
    {
        AntiAliasingPreset aa = index switch
        {
            1 => AntiAliasingPreset.Off,
            2 => AntiAliasingPreset.Fxaa,
            3 => AntiAliasingPreset.Msaa2x,
            4 => AntiAliasingPreset.Msaa4x,
            _ => AntiAliasingPreset.Auto
        };
        _cliOverridesAntiAliasing = false;
        CommitSettingEdit(
            _savedConfig with { AntiAliasing = aa },
            _effectiveSettings with { AntiAliasing = aa },
            "Anti-aliasing applied and saved");
    }

    private void OnAmbientOcclusionSelected(long index)
    {
        AmbientOcclusionPreset ssao = index switch
        {
            1 => AmbientOcclusionPreset.Off,
            2 => AmbientOcclusionPreset.On,
            _ => AmbientOcclusionPreset.Auto
        };
        _cliOverridesSsao = false;
        if (ActiveBenchmarkOverrides is { SsaoMode: not null } activeCli)
        {
            ActiveBenchmarkOverrides = activeCli with { SsaoMode = null };
        }
        CommitSettingEdit(
            _savedConfig with { AmbientOcclusion = ssao },
            _effectiveSettings with { AmbientOcclusion = ssao },
            "Ambient occlusion applied and saved");
    }

    private void OnVsyncToggled(bool toggled)
    {
        _cliOverridesVsync = false;
        _vsyncButton.Text = toggled ? "Enabled" : "Disabled";
        DisplaySettings updatedSaved = _savedConfig with { VSyncEnabled = toggled };
        DisplaySettings updatedEffective = _effectiveSettings with { VSyncEnabled = toggled };
        ApplyToEngine(updatedEffective);
        CommitSettingEdit(updatedSaved, updatedEffective, "Settings applied and saved");
    }

    private void OnFpsSelected(long index)
    {
        int fps = index switch
        {
            1 => 30,
            2 => 60,
            3 => 120,
            4 => 144,
            5 => 165,
            6 => 240,
            _ => 0
        };
        _cliOverridesMaxFps = false;
        DisplaySettings updatedSaved = _savedConfig with { MaxFps = fps };
        DisplaySettings updatedEffective = _effectiveSettings with { MaxFps = fps };
        ApplyToEngine(updatedEffective);
        CommitSettingEdit(updatedSaved, updatedEffective, "Settings applied and saved");
    }

    private void CommitSettingEdit(DisplaySettings saved, DisplaySettings effective, string message)
    {
        _savedConfig = saved;
        _effectiveSettings = effective;
        SaveConfig(_savedConfig);
        SettingsChanged?.Invoke(_effectiveSettings);
        bool hasAnyOverride = _cliOverridesVsync || _cliOverridesMaxFps || _cliOverridesProfile || _cliOverridesQuality || _cliOverridesRenderScale || _cliOverridesAntiAliasing || _cliOverridesSsao;
        _statusLabel.Text = hasAnyOverride ? $"{message} (other CLI overrides active)" : message;
        _statusLabel.AddThemeColorOverride("font_color", hasAnyOverride ? HudTheme.Accent : HudTheme.Muted);
    }

    private static Vector2I ResolveRawWindowPixelSize(Viewport? viewport, Vector2I? windowSizeOverride = null)
    {
        if (windowSizeOverride is { X: > 0, Y: > 0 } overrideSize)
        {
            return overrideSize;
        }

        try
        {
            if (!IsHeadlessDisplay)
            {
                Vector2I winSize = DisplayServer.WindowGetSize();
                if (winSize.X > 0 && winSize.Y > 0) return winSize;
            }
        }
        catch
        {
            // Headless / mock fallback
        }

        if (viewport is Window win && GodotObject.IsInstanceValid(win) && win.Size.X > 0 && win.Size.Y > 0)
        {
            return win.Size;
        }

        if (viewport is not null && GodotObject.IsInstanceValid(viewport))
        {
            Vector2 rect = viewport.GetVisibleRect().Size;
            if (rect.X > 0f && rect.Y > 0f) return new Vector2I((int)rect.X, (int)rect.Y);
        }

        return new Vector2I(ViewportAspectLayout.BaseViewportWidth, ViewportAspectLayout.BaseViewportHeight);
    }

    public static Vector2I ResolveWindowPixelSize(Viewport? viewport, Vector2I? windowSizeOverride = null)
    {
        Vector2I rawSize = ResolveRawWindowPixelSize(viewport, windowSizeOverride);
        ViewportStretchSpec spec = ViewportAspectLayout.ComputeStretchSpec(rawSize.X, rawSize.Y);
        return new Vector2I(spec.ActiveWindowPixelWidth, spec.ActiveWindowPixelHeight);
    }

    public static ViewportStretchSpec ApplyWindowStretchCap(Window? window, Vector2I? pixelSizeOverride = null)
    {
        Vector2I rawSize = ResolveRawWindowPixelSize(window, pixelSizeOverride);
        ViewportStretchSpec spec = ViewportAspectLayout.ComputeStretchSpec(rawSize.X, rawSize.Y);
        if (window is not null && GodotObject.IsInstanceValid(window))
        {
            Vector2I targetContentSize = new(spec.ContentScaleWidth, spec.ContentScaleHeight);
            Window.ContentScaleAspectEnum targetAspect = spec.UseAspectCapKeep
                ? Window.ContentScaleAspectEnum.Keep
                : Window.ContentScaleAspectEnum.Expand;
            if (window.ContentScaleSize != targetContentSize)
            {
                window.ContentScaleSize = targetContentSize;
            }
            if (window.ContentScaleAspect != targetAspect)
            {
                window.ContentScaleAspect = targetAspect;
            }
        }
        return spec;
    }

    public static (float RenderScale, int MsaaSamples) ApplyWindowRenderPolicy(
        Window? window,
        Viewport? viewport,
        DisplaySettings settings,
        Vector2I? windowSizeOverride = null)
    {
        ApplyWindowStretchCap(window, windowSizeOverride);
        return ApplyRenderSettingsToViewport(viewport, settings, windowSizeOverride);
    }

    public static (float RenderScale, int MsaaSamples) ApplyRenderSettingsToViewport(
        Viewport? viewport,
        DisplaySettings settings,
        Vector2I? windowSizeOverride = null)
    {
        Vector2I winSize = ResolveWindowPixelSize(viewport, windowSizeOverride);
        BenchmarkRenderOverrides? cli = ActiveBenchmarkOverrides;
        float scale = cli?.RenderScaleOverride is >= DisplaySettings.MinDiagnosticRenderScale and <= DisplaySettings.MaxAutoRenderScale
            ? cli.RenderScaleOverride.Value
            : DisplaySettings.ResolveRenderScaleFactor(settings.RenderScale, winSize.X, winSize.Y);
        int msaaSamples = DisplaySettings.ResolveMsaaSamples(settings.AntiAliasing, winSize.X, winSize.Y, scale);
        bool useFxaa = DisplaySettings.ResolveScreenSpaceFxaa(settings.AntiAliasing, msaaSamples);
        Viewport.Scaling3DModeEnum targetScalingMode = cli?.ScalingMode switch
        {
            "bilinear" => Viewport.Scaling3DModeEnum.Bilinear,
            "fsr2" => Viewport.Scaling3DModeEnum.Fsr2,
            "fsr" => Viewport.Scaling3DModeEnum.Fsr,
            _ => DisplaySettings.ResolveUseBilinearScaling(winSize.X, winSize.Y, scale)
                ? Viewport.Scaling3DModeEnum.Bilinear
                : Viewport.Scaling3DModeEnum.Fsr
        };
        if (viewport is not null && GodotObject.IsInstanceValid(viewport))
        {
            viewport.Scaling3DScale = scale;
            viewport.Scaling3DMode = targetScalingMode;
            viewport.ScreenSpaceAA = useFxaa
                ? Viewport.ScreenSpaceAAEnum.Fxaa
                : Viewport.ScreenSpaceAAEnum.Disabled;
            viewport.Msaa3D = msaaSamples switch
            {
                4 => Viewport.Msaa.Msaa4X,
                2 => Viewport.Msaa.Msaa2X,
                _ => Viewport.Msaa.Disabled
            };
        }
        return (scale, msaaSamples);
    }

    public static void ApplyToEngine(DisplaySettings settings)
    {
        try
        {
            if (!IsHeadlessDisplay)
            {
                DisplayServer.WindowSetVsyncMode(settings.VSyncEnabled
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
            }
        }
        catch
        {
            // Fallback for mock or headless drivers
        }

        Godot.Engine.MaxFps = settings.MaxFps;
    }

    public static bool TryLoadSavedEnvironmentProfile(out string profile)
    {
        ConfigFile config = new();
        if (config.Load(ConfigPath) == Error.Ok && config.HasSectionKey("display", "environment_profile"))
        {
            profile = DisplaySettings.SanitizeProfile((string)config.GetValue("display", "environment_profile", "overcast"));
            return true;
        }
        profile = "";
        return false;
    }

    public static DisplaySettings LoadConfig()
    {
        ConfigFile config = new();
        Error err = config.Load(ConfigPath);
        if (err != Error.Ok)
        {
            return DisplaySettings.CreateDefault();
        }

        try
        {
            bool vsync = (bool)config.GetValue("display", "vsync", true);
            int maxFps = (int)config.GetValue("display", "max_fps", 0);
            string profile = (string)config.GetValue("display", "environment_profile", "overcast");
            string qualityStr = (string)config.GetValue("display", "environment_quality", "High");
            EnvironmentQuality quality = DisplaySettings.ParseQuality(qualityStr);
            string renderScaleStr = (string)config.GetValue("display", "render_scale", "Native100");
            RenderScalePreset renderScale = DisplaySettings.ParseRenderScale(renderScaleStr, RenderScalePreset.Native100);
            string msaaStr = (string)config.GetValue("display", "msaa", "Auto");
            AntiAliasingPreset antiAliasing = DisplaySettings.ParseAntiAliasing(msaaStr, AntiAliasingPreset.Auto);
            string ssaoStr = (string)config.GetValue("display", "ssao", "Auto");
            AmbientOcclusionPreset ambientOcclusion = DisplaySettings.ParseAmbientOcclusion(ssaoStr, AmbientOcclusionPreset.Auto);
            return new DisplaySettings(
                vsync,
                DisplaySettings.SanitizeMaxFps(maxFps),
                0,
                DisplaySettings.SanitizeProfile(profile),
                quality,
                RenderScale: renderScale,
                AntiAliasing: antiAliasing,
                AmbientOcclusion: ambientOcclusion);
        }
        catch
        {
            return DisplaySettings.CreateDefault();
        }
    }

    public static void SaveConfig(DisplaySettings settings)
    {
        try
        {
            ConfigFile config = new();
            config.SetValue("display", "vsync", settings.VSyncEnabled);
            config.SetValue("display", "max_fps", settings.MaxFps);
            config.SetValue("display", "environment_profile", settings.EnvironmentProfile);
            config.SetValue("display", "environment_quality", settings.Quality.ToString());
            config.SetValue("display", "render_scale", settings.RenderScale.ToString());
            config.SetValue("display", "msaa", settings.AntiAliasing.ToString());
            config.SetValue("display", "ssao", settings.AmbientOcclusion.ToString());
            config.Save(ConfigPath);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Failed to save display settings: {ex.Message}");
        }
    }
}
