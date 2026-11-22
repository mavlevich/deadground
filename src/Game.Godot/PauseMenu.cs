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

        _backdrop = HudTheme.CreateModalBackdrop(visible: false);
        AddChild(_backdrop);

        _card = new PanelContainer
        {
            Theme = HudTheme.Shared,
            Visible = false,
            CustomMinimumSize = new Vector2(320, 240),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        HudTheme.CenterModal(_card);
        _card.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(accented: true, padding: 2, topRail: true, modal: true));
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
        title.AddThemeColorOverride("font_color", HudTheme.Accent);
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
        HudTheme.CenterModal(_card);
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
        btn.AddThemeFontSizeOverride("font_size", 13);
        return btn;
    }
}
