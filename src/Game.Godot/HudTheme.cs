using Godot;

namespace DeadGround.Client;

// Shared presentation resources for the neutral black-graphite HUD.
// World lighting and map features stay independent.
internal static class HudTheme
{
    public static readonly Color Background = new("070708");
    public static readonly Color Panel = new(0.051f, 0.051f, 0.055f, 0.97f);
    public static readonly Color PanelSolid = new("0d0d0e");
    public static readonly Color Surface = new("151517");
    public static readonly Color SurfaceHover = new("202023");
    public static readonly Color Border = new("2c2c30");
    public static readonly Color BorderHighlight = new("45454a");
    public static readonly Color Text = new("e0e0e2");
    public static readonly Color Muted = new("8a8a8f");
    public static readonly Color Accent = new("d8a657");
    public static readonly Color Danger = new("d45b5b");
    public static readonly Color Track = new("1f1f22");
    public static readonly Color Outline = new(0.035f, 0.035f, 0.038f, 0.94f);
    public static readonly Color ModalScrim = new(0.024f, 0.024f, 0.026f, 0.68f);

    private static Theme? _shared;
    private static SystemFont? _uiFont;
    private static SystemFont? _monoFont;
    public static Theme Shared => _shared ??= CreateTheme();
    public static Font UiFont => _uiFont ??= CreateUiFont();
    public static Font MonoFont => _monoFont ??= CreateMonoFont();

    public static void ApplyMono(Control control) => control.AddThemeFontOverride("font", MonoFont);

    private static SystemFont CreateUiFont() => new()
    {
        FontNames = ["DIN Alternate", "Bahnschrift", "Roboto Condensed", "Arial Narrow", "Helvetica Neue", "Arial"],
        Antialiasing = TextServer.FontAntialiasing.Gray,
        Hinting = TextServer.Hinting.Light,
        SubpixelPositioning = TextServer.SubpixelPositioning.Auto
    };

    private static SystemFont CreateMonoFont() => new()
    {
        FontNames = ["JetBrains Mono", "SF Mono", "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New"],
        Antialiasing = TextServer.FontAntialiasing.Gray,
        Hinting = TextServer.Hinting.Light,
        SubpixelPositioning = TextServer.SubpixelPositioning.Auto
    };

    // Center using the completed content size, and keep its center when that size grows.
    public static void CenterModal(Control card)
    {
        card.GrowHorizontal = Control.GrowDirection.Both;
        card.GrowVertical = Control.GrowDirection.Both;
        card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
    }

    public static ColorRect CreateModalBackdrop(bool visible = false)
    {
        ColorRect backdrop = new()
        {
            Theme = Shared,
            Color = ModalScrim,
            Visible = visible,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return backdrop;
    }

    public static StyleBoxFlat CreatePanel(
        bool accented = false,
        int padding = 12,
        bool danger = false,
        bool topRail = false,
        bool modal = false)
    {
        Color border = danger ? Danger : (accented ? Accent : Border);
        StyleBoxFlat style = new()
        {
            BgColor = modal ? PanelSolid : Panel,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = topRail ? 2 : 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2,
            ContentMarginLeft = padding,
            ContentMarginRight = padding,
            ContentMarginTop = padding,
            ContentMarginBottom = padding
        };
        if (modal)
        {
            style.ShadowSize = 18;
            style.ShadowColor = new Color(0f, 0f, 0f, 0.58f);
        }
        return style;
    }

    private static StyleBoxFlat CreateButtonStyle(Color bg, Color border, int padX, int padY, bool activeLed = false) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = activeLed ? 3 : 1,
        BorderWidthRight = 1,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 2,
        CornerRadiusTopRight = 2,
        CornerRadiusBottomLeft = 2,
        CornerRadiusBottomRight = 2,
        ContentMarginLeft = activeLed ? Math.Max(2, padX - 2) : padX,
        ContentMarginRight = padX,
        ContentMarginTop = padY,
        ContentMarginBottom = padY
    };

    private static Theme CreateTheme()
    {
        Theme theme = new() { DefaultFont = UiFont, DefaultFontSize = 13 };
        theme.SetColor("font_color", "Label", Text);
        theme.SetStylebox("panel", "PanelContainer", CreatePanel());

        StyleBoxFlat normal = CreateButtonStyle(Surface, Border, padX: 7, padY: 4);
        StyleBoxFlat hover = CreateButtonStyle(SurfaceHover, Accent, padX: 7, padY: 4);
        StyleBoxFlat pressed = CreateButtonStyle(Background, Accent, padX: 7, padY: 4, activeLed: true);
        StyleBoxFlat hoverPressed = CreateButtonStyle(Surface, Accent, padX: 7, padY: 4, activeLed: true);
        StyleBoxFlat disabled = CreateButtonStyle(Background, Border.Darkened(0.35f), padX: 7, padY: 4);
        StyleBoxFlat focus = CreateButtonStyle(Colors.Transparent, Accent, padX: 7, padY: 4);

        foreach (string type in new[] { "Button", "OptionButton", "CheckButton" })
        {
            theme.SetStylebox("normal", type, normal);
            theme.SetStylebox("hover", type, hover);
            theme.SetStylebox("pressed", type, pressed);
            theme.SetStylebox("hover_pressed", type, hoverPressed);
            theme.SetStylebox("disabled", type, disabled);
            theme.SetStylebox("focus", type, focus);
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Text);
            theme.SetColor("font_pressed_color", type, Accent);
            theme.SetColor("font_hover_pressed_color", type, Accent);
            theme.SetColor("font_focus_color", type, Text);
            theme.SetColor("font_disabled_color", type, Muted.Darkened(0.35f));
        }

        // OptionButton dropdown menus
        StyleBoxFlat popupPanel = CreatePanel(padding: 4, modal: true);
        popupPanel.BgColor = PanelSolid;
        StyleBoxFlat popupHover = CreateButtonStyle(SurfaceHover, Accent, padX: 6, padY: 4);
        theme.SetStylebox("panel", "PopupMenu", popupPanel);
        theme.SetStylebox("hover", "PopupMenu", popupHover);
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Accent);
        theme.SetColor("font_disabled_color", "PopupMenu", Muted.Darkened(0.35f));
        theme.SetColor("font_separator_color", "PopupMenu", Muted);

        // Text input including read-only state while connecting
        StyleBoxFlat inputNormal = CreateButtonStyle(Background, Border, padX: 8, padY: 6);
        StyleBoxFlat inputFocus = CreateButtonStyle(Background, Accent, padX: 8, padY: 6);
        StyleBoxFlat inputReadOnly = CreateButtonStyle(Background, Border.Darkened(0.35f), padX: 8, padY: 6);
        theme.SetStylebox("normal", "LineEdit", inputNormal);
        theme.SetStylebox("focus", "LineEdit", inputFocus);
        theme.SetStylebox("read_only", "LineEdit", inputReadOnly);
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_uneditable_color", "LineEdit", Muted);
        theme.SetColor("font_placeholder_color", "LineEdit", Muted.Darkened(0.25f));
        theme.SetColor("caret_color", "LineEdit", Accent);

        // System hover tooltips
        StyleBoxFlat tooltipStyle = CreatePanel(padding: 6);
        tooltipStyle.BgColor = PanelSolid;
        theme.SetStylebox("panel", "TooltipPanel", tooltipStyle);
        theme.SetColor("font_color", "TooltipLabel", Text);
        theme.SetFontSize("font_size", "TooltipLabel", 11);

        // Compact hardware buttons
        theme.SetTypeVariation("CompactButton", "Button");
        theme.SetFont("font", "CompactButton", MonoFont);
        theme.SetFontSize("font_size", "CompactButton", 10);
        theme.SetStylebox("normal", "CompactButton", CreateButtonStyle(Surface, Border, padX: 5, padY: 2));
        theme.SetStylebox("hover", "CompactButton", CreateButtonStyle(SurfaceHover, Accent, padX: 5, padY: 2));
        theme.SetStylebox("pressed", "CompactButton", CreateButtonStyle(Background, Accent, padX: 5, padY: 2, activeLed: true));
        theme.SetStylebox("hover_pressed", "CompactButton", CreateButtonStyle(Surface, Accent, padX: 5, padY: 2, activeLed: true));
        theme.SetStylebox("disabled", "CompactButton", CreateButtonStyle(Background, Border.Darkened(0.35f), padX: 5, padY: 2));
        theme.SetStylebox("focus", "CompactButton", CreateButtonStyle(Colors.Transparent, Accent, padX: 5, padY: 2));
        theme.SetColor("font_color", "CompactButton", Text);
        theme.SetColor("font_hover_color", "CompactButton", Text);
        theme.SetColor("font_pressed_color", "CompactButton", Accent);
        theme.SetColor("font_hover_pressed_color", "CompactButton", Accent);
        theme.SetColor("font_disabled_color", "CompactButton", Muted.Darkened(0.35f));

        return theme;
    }
}
