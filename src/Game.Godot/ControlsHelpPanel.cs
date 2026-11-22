using Godot;

namespace DeadGround.Client;

internal sealed partial class ControlsHelpPanel : CanvasLayer
{
    private readonly ColorRect _backdrop;
    private readonly PanelContainer _card;
    public bool IsOpen => _card.Visible;
    public Rect2 GetCardRect() => IsOpen ? _card.GetGlobalRect() : default;

    public ControlsHelpPanel()
    {
        Layer = 11;
        _backdrop = HudTheme.CreateModalBackdrop(visible: false);
        AddChild(_backdrop);

        _card = new PanelContainer
        {
            Theme = HudTheme.Shared,
            Visible = false,
            CustomMinimumSize = new Vector2(620, 320),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _card.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(accented: true, padding: 20, topRail: true, modal: true));
        AddChild(_card);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 12);
        _card.AddChild(column);

        Label title = new() { Text = "FIELD CONTROLS", MouseFilter = Control.MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", HudTheme.Accent);
        column.AddChild(title);

        GridContainer table = new() { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        table.AddThemeConstantOverride("h_separation", 18);
        table.AddThemeConstantOverride("v_separation", 8);
        column.AddChild(table);

        AddRow(table, "SELECT", "Left-click or drag a box. Shift adds units; double-click selects a type. Click roster chips to focus.");
        AddRow(table, "ORDERS", "Right-click to move or attack. Right-drag sets final facing. R + RMB reverses.");
        AddRow(table, "FIRE STANCE", "Z: fire at will. X: return fire. C: hold fire.");
        AddRow(table, "CAMERA", "WASD / arrows to pan. Wheel to zoom. Middle-drag or Alt + left-drag to orbit.");
        AddRow(table, "GROUPS", "Ctrl / Cmd + 1-9 stores a group; Shift + 1-9 adds. Tap twice to center.");
        AddRow(table, "RANGES / HUD", "F1 opens help. F7 toggles range limits. F9 toggles diagnostics. F10 opens settings.");
        AddRow(table, "CANCEL", "Escape cancels targeting, then clears selection, then opens the battle menu.");
        AddRow(table, "RANGE KEY", "Solid amber: weapon limit. Dashed neutral: sensor limit. Obstacles may block both.");

        Button close = new() { Text = "CLOSE [ESC]", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += Close;
        column.AddChild(close);
    }

    private static void AddRow(GridContainer table, string heading, string text)
    {
        Label key = new()
        {
            Text = heading,
            CustomMinimumSize = new Vector2(110, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        key.AddThemeFontSizeOverride("font_size", 11);
        key.AddThemeColorOverride("font_color", HudTheme.Accent);
        table.AddChild(key);

        Label body = new()
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(440, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        body.AddThemeFontSizeOverride("font_size", 12);
        body.AddThemeColorOverride("font_color", HudTheme.Text);
        table.AddChild(body);
    }

    public void Open()
    {
        _backdrop.Visible = true;
        _card.Visible = true;
        HudTheme.CenterModal(_card);
    }

    public void Close()
    {
        _card.Visible = false;
        _backdrop.Visible = false;
    }
}
