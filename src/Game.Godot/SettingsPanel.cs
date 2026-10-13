using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// In-game settings overlay for VSync and frame pacing limit configuration.
/// Persists player choices to user://display_settings.cfg with safe default fallback.
/// </summary>
internal sealed partial class SettingsPanel : CanvasLayer
{
    private const string ConfigPath = "user://display_settings.cfg";
    private readonly Control _backdrop;
    private readonly PanelContainer _container;
    private readonly CheckButton _vsyncButton;
    private readonly OptionButton _fpsOptions;
    private readonly Label _statusLabel;
    private DisplaySettings _savedConfig = DisplaySettings.CreateDefault();
    private DisplaySettings _effectiveSettings = DisplaySettings.CreateDefault();
    private bool _cliOverridesVsync;
    private bool _cliOverridesMaxFps;

    public DisplaySettings CurrentSettings => _effectiveSettings;
    public DisplaySettings SavedConfig => _savedConfig;
    public bool IsOpen => _container.Visible;
    public Rect2 GetContainerRect() => _container.Visible ? _container.GetGlobalRect() : default;

    public SettingsPanel()
    {
        Layer = 10;

        _backdrop = new Control
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);

        _container = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(360, 260)
        };
        _container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        _container.AddThemeStyleboxOverride("panel", PanelStyle());
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
            Text = "DISPLAY SETTINGS",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(title);

        // VSync Row
        HBoxContainer vsyncRow = new();
        vsyncRow.AddThemeConstantOverride("separation", 12);
        Label vsyncLabel = new() { Text = "Vertical Sync (VSync):", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vsyncLabel.AddThemeColorOverride("font_color", new Color("d5ded0"));
        _vsyncButton = new CheckButton { Text = "Enabled" };
        _vsyncButton.Toggled += OnVsyncToggled;
        vsyncRow.AddChild(vsyncLabel);
        vsyncRow.AddChild(_vsyncButton);
        stack.AddChild(vsyncRow);

        // FPS Limit Row
        HBoxContainer fpsRow = new();
        fpsRow.AddThemeConstantOverride("separation", 12);
        Label fpsLabel = new() { Text = "Frame Rate Cap:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fpsLabel.AddThemeColorOverride("font_color", new Color("d5ded0"));
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
        _statusLabel.AddThemeColorOverride("font_color", new Color("889988"));
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
        bool cliOverridesMaxFps)
    {
        _savedConfig = savedConfig.WithSanitizedValues();
        _effectiveSettings = effectiveSettings.WithSanitizedValues();
        _cliOverridesVsync = cliOverridesVsync;
        _cliOverridesMaxFps = cliOverridesMaxFps;

        ApplyToEngine(_effectiveSettings);
        UpdateUiFromSettings();

        if (cliOverridesVsync || cliOverridesMaxFps)
        {
            string detail = (cliOverridesVsync && cliOverridesMaxFps)
                ? "VSync and FPS limit overridden via command-line arguments"
                : (cliOverridesVsync ? "VSync overridden via command-line arguments" : "FPS limit overridden via command-line arguments");
            _statusLabel.Text = detail;
            _statusLabel.AddThemeColorOverride("font_color", new Color("edc76b"));
        }
    }

    public void Initialize(DisplaySettings initialSettings, bool wasOverriddenByCli = false) =>
        Initialize(initialSettings, initialSettings, wasOverriddenByCli, wasOverriddenByCli);

    public void Open()
    {
        UpdateUiFromSettings();
        _backdrop.Visible = true;
        _container.Visible = true;
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
    }

    private void OnVsyncToggled(bool toggled)
    {
        _savedConfig = _savedConfig with { VSyncEnabled = toggled };
        _effectiveSettings = _effectiveSettings with { VSyncEnabled = toggled };
        _cliOverridesVsync = false;
        _vsyncButton.Text = toggled ? "Enabled" : "Disabled";
        ApplyToEngine(_effectiveSettings);
        SaveConfig(_savedConfig);
        _statusLabel.Text = _cliOverridesMaxFps
            ? "VSync saved (FPS limit still overridden by CLI)"
            : "Settings applied and saved";
        _statusLabel.AddThemeColorOverride("font_color", new Color("889988"));
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
        _savedConfig = _savedConfig with { MaxFps = fps };
        _effectiveSettings = _effectiveSettings with { MaxFps = fps };
        _cliOverridesMaxFps = false;
        ApplyToEngine(_effectiveSettings);
        SaveConfig(_savedConfig);
        _statusLabel.Text = _cliOverridesVsync
            ? "FPS limit saved (VSync still overridden by CLI)"
            : "Settings applied and saved";
        _statusLabel.AddThemeColorOverride("font_color", new Color("889988"));
    }

    public static void ApplyToEngine(DisplaySettings settings)
    {
        try
        {
            if (DisplayServer.GetName() != "headless")
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
            return new DisplaySettings(vsync, DisplaySettings.SanitizeMaxFps(maxFps));
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
            config.Save(ConfigPath);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Failed to save display settings: {ex.Message}");
        }
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.055f, 0.08f, 0.065f, 0.96f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 2,
        BorderWidthTop = 2,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8
    };
}
