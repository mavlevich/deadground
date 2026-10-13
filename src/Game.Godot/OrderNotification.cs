using Godot;

namespace DeadGround.Client;

// Manages temporary action notifications independently of selection card updates.
internal sealed partial class OrderNotification : PanelContainer
{
    public const float DefaultDurationSeconds = 3.0f;
    private readonly Label _label;
    private double _remainingSeconds;

    public OrderNotification()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(320, 32);

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.12f, 0.14f, 0.18f, 0.92f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.4f, 0.6f, 0.8f, 0.7f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        AddThemeStyleboxOverride("panel", style);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _label.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_label);

        Visible = false;
    }

    public void ShowNotification(string text, float duration = DefaultDurationSeconds)
    {
        _label.Text = text;
        _remainingSeconds = duration;
        Visible = true;
        Modulate = Colors.White;
    }

    public void Update(double delta)
    {
        if (!Visible) return;

        _remainingSeconds -= delta;
        if (_remainingSeconds <= 0)
        {
            Visible = false;
            _label.Text = string.Empty;
        }
        else if (_remainingSeconds < 0.5)
        {
            // Smooth fade out in the final 0.5s
            float alpha = (float)(_remainingSeconds / 0.5);
            Modulate = new Color(1, 1, 1, alpha);
        }
    }

    public string CurrentText => _label.Text;

    public void Clear()
    {
        Visible = false;
        _remainingSeconds = 0;
        _label.Text = string.Empty;
    }
}
