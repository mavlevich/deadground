using Godot;

namespace DeadGround.Client;

internal sealed partial class PauseMenu : CanvasLayer
{
    private readonly Control _backdrop;
    private readonly PanelContainer _card;

    public event Action? ResumeRequested;
    public event Action? SettingsRequested;
    public event Action? LeaveRequested;
    public event Action? ExitRequested;

    public bool IsOpen => _card.Visible;

    public PauseMenu()
    {
        Layer = 9;

        _backdrop = new Control
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);

        _card = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(320, 240),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        _card.AddThemeStyleboxOverride("panel", CardStyle());
        AddChild(_card);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        _card.AddChild(margin);

        VBoxContainer stack = new();
        stack.AddThemeConstantOverride("separation", 10);
        margin.AddChild(stack);

        Label title = new()
        {
            Text = "BATTLE MENU",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(title);

        Control spacer = new() { CustomMinimumSize = new Vector2(0, 6) };
        stack.AddChild(spacer);

        Button resumeBtn = CreateButton("RESUME [ESC]");
        resumeBtn.Pressed += () =>
        {
            Close();
            ResumeRequested?.Invoke();
        };
        stack.AddChild(resumeBtn);

        Button settingsBtn = CreateButton("SETTINGS [F10]");
        settingsBtn.Pressed += () => SettingsRequested?.Invoke();
        stack.AddChild(settingsBtn);

        Button leaveBtn = CreateButton("LEAVE BATTLE");
        leaveBtn.Pressed += () =>
        {
            Close();
            LeaveRequested?.Invoke();
        };
        stack.AddChild(leaveBtn);

        Button exitBtn = CreateButton("EXIT GAME");
        exitBtn.Pressed += () => ExitRequested?.Invoke();
        stack.AddChild(exitBtn);
    }

    public void Open()
    {
        _backdrop.Visible = true;
        _card.Visible = true;
    }

    public void Close()
    {
        _backdrop.Visible = false;
        _card.Visible = false;
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public Rect2 GetCardRect() => _card.Visible ? _card.GetGlobalRect() : default;

    private static Button CreateButton(string text)
    {
        Button btn = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 36),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        btn.AddThemeStyleboxOverride("normal", ButtonNormalStyle());
        btn.AddThemeStyleboxOverride("hover", ButtonHoverStyle());
        btn.AddThemeStyleboxOverride("pressed", ButtonPressedStyle());
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.AddThemeColorOverride("font_color", new Color("d5ded0"));
        btn.AddThemeColorOverride("font_hover_color", new Color("ffffff"));
        btn.AddThemeColorOverride("font_pressed_color", new Color("edc76b"));
        return btn;
    }

    private static StyleBoxFlat CardStyle() => new()
    {
        BgColor = new Color(0.04f, 0.06f, 0.05f, 0.96f),
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

    private static StyleBoxFlat ButtonNormalStyle() => new()
    {
        BgColor = new Color(0.10f, 0.14f, 0.11f, 0.92f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonHoverStyle() => new()
    {
        BgColor = new Color(0.16f, 0.22f, 0.17f, 0.95f),
        BorderColor = new Color("edc76b"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonPressedStyle() => new()
    {
        BgColor = new Color(0.08f, 0.11f, 0.09f, 0.98f),
        BorderColor = new Color("77dd77"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };
}
