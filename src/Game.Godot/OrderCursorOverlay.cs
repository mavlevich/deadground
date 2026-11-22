using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// 2D HUD overlay displaying the move order cursor indicator and contextual tooltip.
/// Allocation-free in steady-state frames with unchanged preview state.
/// MouseFilter is set to Ignore to allow input events to pass through.
/// </summary>
internal sealed partial class OrderCursorOverlay : Control
{
    private sealed partial class OrderIconControl : Control
    {
        private static readonly Color ColorBlocked = HudTheme.Danger;
        private static readonly Color ColorValid = HudTheme.Text;
        private static readonly Color ColorAdjusted = HudTheme.Accent;
        private static readonly Color ColorMixed = HudTheme.Accent;

        public DestinationValidity Validity { get; set; } = DestinationValidity.Valid;
        public bool IsMixed { get; set; }

        public OrderIconControl()
        {
            CustomMinimumSize = new Vector2(18, 18);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            Vector2 center = new(9f, 9f);

            if (Validity == DestinationValidity.Blocked)
            {
                // Forbidden symbol: circle with diagonal line
                DrawArc(center, 7f, 0f, MathF.Tau, 24, ColorBlocked, 1.8f);
                float cos = MathF.Cos(MathF.PI * 0.25f) * 7f;
                float sin = MathF.Sin(MathF.PI * 0.25f) * 7f;
                DrawLine(new Vector2(center.X - cos, center.Y - sin), new Vector2(center.X + cos, center.Y + sin), ColorBlocked, 1.8f);
            }
            else if (IsMixed)
            {
                // Group / mixed icon: double ring / split
                DrawArc(center, 7f, 0f, MathF.Tau, 24, ColorMixed, 1.5f);
                DrawCircle(center, 3.5f, ColorMixed);
            }
            else if (Validity == DestinationValidity.Adjusted)
            {
                // Adjusted icon: crosshair with small offset snap dot
                DrawArc(center, 6.5f, 0f, MathF.Tau, 20, ColorAdjusted, 1.4f);
                DrawLine(new Vector2(center.X - 8, center.Y), new Vector2(center.X + 8, center.Y), ColorAdjusted, 1.2f);
                DrawLine(new Vector2(center.X, center.Y - 8), new Vector2(center.X, center.Y + 8), ColorAdjusted, 1.2f);
                DrawCircle(new Vector2(center.X + 3.5f, center.Y - 3.5f), 1.8f, ColorAdjusted);
            }
            else
            {
                // Valid move: crisp target reticle
                DrawArc(center, 6.5f, 0f, MathF.Tau, 20, ColorValid, 1.5f);
                DrawLine(new Vector2(center.X - 8, center.Y), new Vector2(center.X + 8, center.Y), ColorValid, 1.2f);
                DrawLine(new Vector2(center.X, center.Y - 8), new Vector2(center.X, center.Y + 8), ColorValid, 1.2f);
                DrawCircle(center, 2f, ColorValid);
            }
        }
    }

    private readonly PanelContainer _panel;
    private readonly Label _label;
    private readonly OrderIconControl _icon;

    // Cache to prevent string allocations and UI updates in steady state
    private bool _hasState;
    private bool _lastVisible;
    private DestinationValidity _lastValidity;
    private DestinationBlockReason _lastReason;
    private int _lastValidCount;
    private int _lastTotalCount;
    private bool _lastIsReverse;

    internal bool IsPanelVisible => _panel.Visible;
    internal Rect2 PanelRect => _panel.GetGlobalRect();
    internal DestinationValidity CurrentValidity => _lastValidity;
    internal DestinationBlockReason CurrentReason => _lastReason;
    internal string CurrentText => _label.Text;
    internal bool CurrentIsMixed => _icon.IsMixed;
    internal bool CurrentIsReverse => _lastIsReverse;

    public OrderCursorOverlay()
    {
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        StyleBoxFlat style = HudTheme.CreatePanel(padding: 4);
        style.ContentMarginLeft = 8;
        style.ContentMarginRight = 10;

        _panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _panel.AddThemeStyleboxOverride("panel", style);

        HBoxContainer hbox = new()
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        hbox.AddThemeConstantOverride("separation", 8);

        _icon = new OrderIconControl();
        hbox.AddChild(_icon);

        _label = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        _label.AddThemeFontSizeOverride("font_size", 12);
        _label.AddThemeColorOverride("font_color", HudTheme.Text);
        hbox.AddChild(_label);

        _panel.AddChild(hbox);
        AddChild(_panel);

        Visible = true;
    }

    public void SetPreviewState(
        bool visible,
        DestinationValidity validity,
        DestinationBlockReason reason,
        int validCount,
        int totalCount,
        bool isReverse = false)
    {
        if (_hasState &&
            _lastVisible == visible &&
            _lastValidity == validity &&
            _lastReason == reason &&
            _lastValidCount == validCount &&
            _lastTotalCount == totalCount &&
            _lastIsReverse == isReverse)
        {
            return;
        }

        _hasState = true;
        _lastVisible = visible;
        _lastValidity = validity;
        _lastReason = reason;
        _lastValidCount = validCount;
        _lastTotalCount = totalCount;
        _lastIsReverse = isReverse;

        if (!visible)
        {
            _panel.Visible = false;
            Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
            return;
        }

        _panel.Visible = true;

        bool isMixed = totalCount > 1 && validCount > 0 && validCount < totalCount;
        _icon.Validity = validity;
        _icon.IsMixed = isMixed;
        _icon.QueueRedraw();

        if (validity == DestinationValidity.Blocked)
        {
            Input.SetDefaultCursorShape(Input.CursorShape.Forbidden);
            if (reason == DestinationBlockReason.NoReverseGear)
            {
                _label.Text = DestinationBlockReasons.NoReverseGear;
            }
            else if (totalCount > 1 && validCount == 0)
            {
                string reasonText = DestinationBlockReasons.GetReasonText(reason);
                _label.Text = string.IsNullOrEmpty(reasonText)
                    ? $"Cannot move ({totalCount} units)"
                    : reasonText;
            }
            else
            {
                _label.Text = DestinationBlockReasons.GetReasonText(reason);
            }
        }
        else
        {
            Input.SetDefaultCursorShape(Input.CursorShape.Cross);

            string actionText = isReverse ? "[REVERSE] " : string.Empty;

            if (isMixed)
            {
                _label.Text = $"{actionText}Destination suitable for {validCount} of {totalCount} units.";
            }
            else if (validity == DestinationValidity.Adjusted)
            {
                _label.Text = $"{actionText}Move (adjusted to clear position)";
            }
            else
            {
                _label.Text = $"{actionText}Move";
            }
        }
    }

    public void UpdatePosition(Vector2 mousePos, Vector2 viewportSize)
    {
        if (!_panel.Visible) return;

        Vector2 panelSize = _panel.GetCombinedMinimumSize();
        _panel.Size = panelSize;
        float posX = mousePos.X + 18f;
        float posY = mousePos.Y + 18f;

        // Keep inside screen boundaries
        if (posX + panelSize.X > viewportSize.X - 10f)
        {
            posX = mousePos.X - panelSize.X - 8f;
        }
        if (posY + panelSize.Y > viewportSize.Y - 10f)
        {
            posY = mousePos.Y - panelSize.Y - 8f;
        }

        _panel.Position = new Vector2(
            Mathf.Clamp(posX, 4f, MathF.Max(4f, viewportSize.X - panelSize.X - 10f)),
            Mathf.Clamp(posY, 4f, MathF.Max(4f, viewportSize.Y - panelSize.Y - 10f)));
    }

    public void HideTooltip()
    {
        if (_panel.Visible)
        {
            _panel.Visible = false;
            _hasState = false;
            Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
        }
    }

    public void SetCustomTooltip(bool visible, DestinationValidity validity, string text)
    {
        if (!visible)
        {
            HideTooltip();
            return;
        }

        _hasState = false;
        _panel.Visible = true;
        _icon.Validity = validity;
        _icon.IsMixed = false;
        _icon.QueueRedraw();
        _label.Text = text;
        Input.SetDefaultCursorShape(validity == DestinationValidity.Blocked ? Input.CursorShape.Forbidden : Input.CursorShape.Cross);
    }
}
