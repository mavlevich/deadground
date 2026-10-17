using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal enum RoleBadgeAffiliation
{
    Friendly,
    EnemyIdentified,
    EnemyUnknown,
    EnemyLastKnown
}

internal sealed partial class RoleBadge : PanelContainer
{
    private readonly Label _label;
    private static readonly StyleBoxFlat FriendlyStyle = CreateStyle(new Color("203322"), new Color("5cd65c"));
    private static readonly StyleBoxFlat FriendlyTankStyle = CreateStyle(new Color("202e33"), new Color("edc76b"));
    private static readonly StyleBoxFlat EnemyIdentifiedStyle = CreateStyle(new Color("3b1814"), new Color("ff4020"));
    private static readonly StyleBoxFlat EnemyCommandStyle = CreateStyle(new Color("441520"), new Color("ff3366"));
    private static readonly StyleBoxFlat EnemyUnknownStyle = CreateStyle(new Color("2b1919"), new Color("e04b36"));
    private static readonly StyleBoxFlat EnemyLastKnownStyle = CreateStyle(new Color("241a18"), new Color("a86658", 0.75f));

    private EntityId? _lastUnitId;
    private string? _lastRoleText;
    private int _lastControlGroup = -1;
    private ContactId? _lastContactId;
    private ContactState _lastState;
    private DetectionLevel _lastLevel;
    private ContactClassification _lastClassification;
    private int _lastAgeRounded = -1;
    private StyleBoxFlat? _lastStyle;

    public EntityId? BoundUnitId { get; private set; }
    public ContactId? BoundContactId { get; private set; }
    public int BoundControlGroup => _lastControlGroup;
    public string LabelText => _label.Text;
    public Vector2 ScreenCenter { get; private set; }

    public RoleBadge()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(40, 18);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _label.AddThemeFontSizeOverride("font_size", 10);
        AddChild(_label);
    }

    public void BindUnit(EntityId id, string roleText, int controlGroup = 0)
    {
        BoundUnitId = id;
        BoundContactId = null;

        if (_lastUnitId != id || !string.Equals(_lastRoleText, roleText, StringComparison.Ordinal) || _lastControlGroup != controlGroup)
        {
            _lastUnitId = id;
            _lastRoleText = roleText;
            _lastControlGroup = controlGroup;
            _label.Text = controlGroup > 0 ? $"{roleText} {controlGroup}" : roleText;
            StyleBoxFlat style = roleText.Contains("TANK", StringComparison.OrdinalIgnoreCase) ? FriendlyTankStyle : FriendlyStyle;
            if (!ReferenceEquals(_lastStyle, style))
            {
                _lastStyle = style;
                AddThemeStyleboxOverride("panel", style);
            }
        }
    }

    public void BindContact(ContactId id, ContactState state, DetectionLevel level, ContactClassification classification, float ageSeconds)
    {
        BoundUnitId = null;
        BoundContactId = id;
        _lastControlGroup = 0;
        int ageRounded = (int)MathF.Round(ageSeconds);

        if (_lastContactId != id || _lastState != state || _lastLevel != level || _lastClassification != classification || (state == ContactState.LastKnown && _lastAgeRounded != ageRounded))
        {
            _lastContactId = id;
            _lastState = state;
            _lastLevel = level;
            _lastClassification = classification;
            _lastAgeRounded = ageRounded;

            StyleBoxFlat style;
            if (state == ContactState.LastKnown)
            {
                string classStr = level == DetectionLevel.Identified ? GetShortClass(classification) : "?";
                _label.Text = $"{classStr} {ageRounded}s";
                style = EnemyLastKnownStyle;
            }
            else if (level == DetectionLevel.Identified && classification != ContactClassification.Unknown)
            {
                _label.Text = GetShortClass(classification);
                style = classification == ContactClassification.CommandVehicle ? EnemyCommandStyle : EnemyIdentifiedStyle;
            }
            else
            {
                _label.Text = "[ ? ]";
                style = EnemyUnknownStyle;
            }

            if (!ReferenceEquals(_lastStyle, style))
            {
                _lastStyle = style;
                AddThemeStyleboxOverride("panel", style);
            }
        }
    }

    public void UpdateScreenPosition(Vector2 center, float opacity = 1.0f)
    {
        ScreenCenter = center;
        Vector2 size = GetCombinedMinimumSize();
        Size = size;
        Position = new Vector2(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f);
        Modulate = new Color(1, 1, 1, opacity);
    }

    public bool ContainsScreenPoint(Vector2 screenPoint)
    {
        if (!Visible || Modulate.A < 0.1f) return false;
        Vector2 minSize = GetCombinedMinimumSize();
        float width = Math.Max(Size.X, Math.Max(40f, minSize.X));
        float height = Math.Max(Size.Y, Math.Max(18f, minSize.Y));
        Rect2 rect = new Rect2(GlobalPosition, new Vector2(width, height));
        // Expand hit area slightly (4px padding) for easier clicking
        return rect.Grow(4f).HasPoint(screenPoint);
    }

    private static string GetShortClass(ContactClassification classification) => classification switch
    {
        ContactClassification.Tank => "TANK",
        ContactClassification.Recon => "RECON",
        ContactClassification.Apc => "APC",
        ContactClassification.CommandVehicle => "CMD",
        ContactClassification.Vehicle => "VEH",
        _ => "UNKNOWN"
    };

    private static StyleBoxFlat CreateStyle(Color bg, Color border) => new()
    {
        BgColor = new Color(bg.R, bg.G, bg.B, 0.85f),
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        BorderColor = border,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
        ContentMarginLeft = 4,
        ContentMarginRight = 4,
        ContentMarginTop = 1,
        ContentMarginBottom = 1
    };
}
