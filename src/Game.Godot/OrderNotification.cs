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
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(320, 32);

        StyleBoxFlat style = HudTheme.CreatePanel(accented: true, padding: 6, topRail: true);
        style.ContentMarginLeft = 12;
        style.ContentMarginRight = 12;
        AddThemeStyleboxOverride("panel", style);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
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
