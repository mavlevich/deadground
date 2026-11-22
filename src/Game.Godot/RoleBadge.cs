using DeadGround.Engine;
using DeadGround.Sandbox;
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
    private readonly PanelContainer _headerPill;
    private readonly Label _label;
    private readonly Label _metaLabel;
    private readonly UnitRoleSymbol _symbol;
    private readonly UnitReadinessBars _bars;
    private readonly Label _readinessLabel;

    private static readonly StyleBoxEmpty OuterEmptyStyle = new()
    {
        ContentMarginLeft = 0,
        ContentMarginRight = 0,
        ContentMarginTop = 0,
        ContentMarginBottom = 0
    };
    private static readonly StyleBoxFlat PillFriendlyStyle = CreatePillStyle(HudTheme.Border);
    private static readonly StyleBoxFlat PillSelectedStyle = CreatePillStyle(HudTheme.Accent);
    private static readonly StyleBoxFlat PillEnemyStyle = CreatePillStyle(HudTheme.Danger);
    private static readonly StyleBoxFlat PillEnemyLastKnownStyle = CreatePillStyle(HudTheme.Danger.Darkened(0.32f));

    private EntityId? _lastUnitId;
    private string? _lastRoleText;
    private int _lastControlGroup = -1;
    private ContactId? _lastContactId;
    private ContactState _lastState;
    private ContactClassification _lastClassification;
    private int _lastAgeRounded = -1;
    private StyleBoxFlat? _lastPillStyle;
    private WeaponReadinessState? _displayedReadinessState;
    private bool _lastSelected;
    private FireStance _lastStance = FireStance.FireAtWill;
    private byte _lastPassengers;
    private byte _lastCapacity;

    public EntityId? BoundUnitId { get; private set; }
    public ContactId? BoundContactId { get; private set; }
    public int BoundControlGroup => _lastControlGroup;
    public string LabelText => _label.Text;
    public Vector2 ScreenCenter { get; private set; }
    internal bool ReadinessVisible => _bars.Visible;
    internal WeaponReadinessPresentation Readiness => _bars.Readiness;
    internal string ReadinessText => _readinessLabel.Text;
    internal string MetaText => _metaLabel.Text;

    public RoleBadge()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = HudTheme.Shared;
        CustomMinimumSize = new Vector2(28, 16);
        AddThemeStyleboxOverride("panel", OuterEmptyStyle);

        VBoxContainer column = new() { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 2);
        AddChild(column);

        // Compact dark obsidian pill wraps only the icon and name for sharp contrast on any terrain.
        _headerPill = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _headerPill.AddThemeStyleboxOverride("panel", PillFriendlyStyle);
        _lastPillStyle = PillFriendlyStyle;
        column.AddChild(_headerPill);

        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        row.AddThemeConstantOverride("separation", 3);
        _headerPill.AddChild(row);

        _symbol = new UnitRoleSymbol();
        row.AddChild(_symbol);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        HudTheme.ApplyMono(_label);
        _label.AddThemeFontSizeOverride("font_size", 9);
        _label.AddThemeColorOverride("font_color", HudTheme.Text);
        row.AddChild(_label);

        _metaLabel = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        HudTheme.ApplyMono(_metaLabel);
        _metaLabel.AddThemeFontSizeOverride("font_size", 9);
        _metaLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        row.AddChild(_metaLabel);

        _bars = new UnitReadinessBars(compact: true) { Visible = false };
        column.AddChild(_bars);

        _readinessLabel = new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        HudTheme.ApplyMono(_readinessLabel);
        _readinessLabel.AddThemeFontSizeOverride("font_size", 8);
        _readinessLabel.AddThemeConstantOverride("outline_size", 3);
        _readinessLabel.AddThemeColorOverride("font_outline_color", HudTheme.Outline);
        _readinessLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        column.AddChild(_readinessLabel);
    }

    private void SetPillStyle(StyleBoxFlat style)
    {
        if (ReferenceEquals(_lastPillStyle, style)) return;
        _lastPillStyle = style;
        _headerPill.AddThemeStyleboxOverride("panel", style);
    }

    public void BindUnit(EntityId id, string roleText, int controlGroup = 0, ContactClassification classification = ContactClassification.Unknown)
    {
        BoundUnitId = id;
        BoundContactId = null;
        _lastClassification = classification;
        _symbol.Update(classification, false, false, _lastSelected);

        if (_lastUnitId != id || !string.Equals(_lastRoleText, roleText, StringComparison.Ordinal) || _lastControlGroup != controlGroup)
        {
            _lastUnitId = id;
            _lastRoleText = roleText;
            _lastControlGroup = controlGroup;
            _label.Text = controlGroup > 0 ? $"{roleText} {controlGroup}" : roleText;
            _label.AddThemeColorOverride("font_color", HudTheme.Text);
            SetPillStyle(_lastSelected ? PillSelectedStyle : PillFriendlyStyle);
        }
    }

    public void UpdateOwnReadiness(
        float health,
        WeaponReadinessPresentation readiness,
        bool showDetails,
        bool selected = false,
        bool showLabel = true,
        FireStance stance = FireStance.FireAtWill,
        byte passengerCount = 0,
        byte passengerCapacity = 0)
    {
        if (_lastSelected != selected)
        {
            _lastSelected = selected;
            _symbol.Update(_lastClassification, false, false, selected);
            SetPillStyle(selected ? PillSelectedStyle : PillFriendlyStyle);
        }

        _label.Visible = showLabel || selected;
        _bars.Update(health, readiness);
        _bars.Visible = showDetails;

        _readinessLabel.Visible = showDetails && readiness.State is WeaponReadinessState.Reloading or WeaponReadinessState.Empty;
        if (_displayedReadinessState != readiness.State)
        {
            _displayedReadinessState = readiness.State;
            _readinessLabel.Text = readiness.Label;
            _readinessLabel.AddThemeColorOverride("font_color", readiness.State == WeaponReadinessState.Empty ? HudTheme.Danger : HudTheme.Accent);
        }

        if (_lastStance != stance || _lastPassengers != passengerCount || _lastCapacity != passengerCapacity)
        {
            _lastStance = stance;
            _lastPassengers = passengerCount;
            _lastCapacity = passengerCapacity;
            UpdateMetaLabel();
        }
        _metaLabel.Visible = _metaLabel.Text.Length > 0 && (_label.Visible || passengerCount > 0 || stance == FireStance.HoldFire);
    }

    private void UpdateMetaLabel()
    {
        if (_lastCapacity > 0 && _lastPassengers > 0)
        {
            _metaLabel.Text = _lastStance == FireStance.HoldFire
                ? $"[{_lastPassengers}/{_lastCapacity}] HOLD"
                : $"[{_lastPassengers}/{_lastCapacity}]";
            _metaLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
            return;
        }
        if (_lastStance == FireStance.HoldFire)
        {
            _metaLabel.Text = "HOLD";
            _metaLabel.AddThemeColorOverride("font_color", HudTheme.Danger);
            return;
        }
        _metaLabel.Text = string.Empty;
    }

    public void BindContact(ContactId id, ContactState state, string roleText, ContactClassification classification, float ageSeconds)
    {
        BoundUnitId = null;
        BoundContactId = id;
        _lastSelected = false;
        _bars.Visible = false;
        _readinessLabel.Visible = false;
        _metaLabel.Visible = false;
        _label.Visible = true;
        _symbol.Update(classification, true, state == ContactState.LastKnown, false);
        _lastControlGroup = 0;
        int ageRounded = (int)MathF.Round(ageSeconds);

        if (_lastContactId != id || _lastState != state || !string.Equals(_lastRoleText, roleText, StringComparison.Ordinal) || _lastClassification != classification || (state == ContactState.LastKnown && _lastAgeRounded != ageRounded))
        {
            _lastContactId = id;
            _lastState = state;
            _lastRoleText = roleText;
            _lastClassification = classification;
            _lastAgeRounded = ageRounded;

            if (state == ContactState.LastKnown)
            {
                _label.Text = $"{roleText} {ageRounded}s";
                _label.AddThemeColorOverride("font_color", HudTheme.Danger.Darkened(0.12f));
                SetPillStyle(PillEnemyLastKnownStyle);
            }
            else
            {
                _label.Text = roleText;
                _label.AddThemeColorOverride("font_color", HudTheme.Danger);
                SetPillStyle(PillEnemyStyle);
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
        float width = Math.Max(Size.X, Math.Max(28f, minSize.X));
        float height = Math.Max(Size.Y, Math.Max(16f, minSize.Y));
        Rect2 rect = new Rect2(GlobalPosition, new Vector2(width, height));
        // Expand hit area slightly (4px padding) for easier clicking
        return rect.Grow(4f).HasPoint(screenPoint);
    }

    private static StyleBoxFlat CreatePillStyle(Color border) => new()
    {
        BgColor = new Color(HudTheme.PanelSolid.R, HudTheme.PanelSolid.G, HudTheme.PanelSolid.B, 0.88f),
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        BorderColor = border,
        CornerRadiusTopLeft = 2,
        CornerRadiusTopRight = 2,
        CornerRadiusBottomLeft = 2,
        CornerRadiusBottomRight = 2,
        ContentMarginLeft = 4,
        ContentMarginRight = 4,
        ContentMarginTop = 1,
        ContentMarginBottom = 1
    };
}
