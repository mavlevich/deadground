using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

public enum ContextTargetingMode
{
    None = 0,
    BoardTransport = 1,
    Disembark = 2
}

/// <summary>
/// Contextual action panel displaying boarding and disembarking operations for infantry and transports.
/// Zero heap allocations in steady-state frames with unchanged selection and transport state.
/// </summary>
internal sealed partial class ContextActionPanel : PanelContainer
{
    private readonly Button _boardButton;
    private readonly Button _disembarkHereButton;
    private readonly Button _disembarkButton;
    private readonly Label _passengerLabel;
    private readonly Label _statusLabel;
    private UnitCatalog _catalog;

    public ContextTargetingMode TargetingMode { get; private set; } = ContextTargetingMode.None;

    internal Button BoardButton => _boardButton;
    internal Button DisembarkHereButton => _disembarkHereButton;
    internal Button DisembarkButton => _disembarkButton;
    internal Label PassengerLabel => _passengerLabel;
    internal Label StatusLabel => _statusLabel;

    public event Action<ContextTargetingMode>? TargetingModeChanged;
    public event Action? DisembarkHereRequested;

    // Cached state for zero-allocation steady-state updates
    private int _cachedSelectionCount = -1;
    private uint _cachedSelectionHash;
    private ContextTargetingMode _cachedTargetingMode = (ContextTargetingMode)(-1);
    private int _cachedPassengerCount = -1;
    private int _cachedPassengerCapacity = -1;
    private EntityId _cachedTransportId;

    public ContextActionPanel(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(HudLayout.CardWidth, HudLayout.ContextHeight);

        StyleBoxFlat style = HudTheme.CreatePanel(padding: 4, topRail: true);
        style.ContentMarginLeft = 10;
        style.ContentMarginRight = 10;
        AddThemeStyleboxOverride("panel", style);

        VBoxContainer column = new() { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 2);
        AddChild(column);
        HBoxContainer hbox = new()
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        hbox.AddThemeConstantOverride("separation", 6);
        column.AddChild(hbox);

        _boardButton = new Button
        {
            Text = "BOARD TRANSPORT",
            ThemeTypeVariation = "CompactButton",
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(110, 22)
        };
        _boardButton.Pressed += OnBoardButtonPressed;
        hbox.AddChild(_boardButton);

        _disembarkHereButton = new Button
        {
            Text = "UNLOAD HERE",
            ThemeTypeVariation = "CompactButton",
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(88, 22)
        };
        _disembarkHereButton.Pressed += () => DisembarkHereRequested?.Invoke();
        hbox.AddChild(_disembarkHereButton);

        _disembarkButton = new Button
        {
            Text = "UNLOAD AT...",
            ThemeTypeVariation = "CompactButton",
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(88, 22)
        };
        _disembarkButton.Pressed += OnDisembarkButtonPressed;
        hbox.AddChild(_disembarkButton);

        _passengerLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        HudTheme.ApplyMono(_passengerLabel);
        _passengerLabel.AddThemeFontSizeOverride("font_size", 10);
        _passengerLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        hbox.AddChild(_passengerLabel);

        _statusLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 10);
        _statusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        column.AddChild(_statusLabel);

        Visible = false;
        _boardButton.Visible = false;
        _disembarkHereButton.Visible = false;
        _disembarkButton.Visible = false;
        _passengerLabel.Visible = false;
        _statusLabel.Visible = false;
    }

    internal void SetCatalog(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        CancelTargeting();
        _cachedSelectionCount = -1;
        _cachedSelectionHash = 0;
        _cachedTargetingMode = (ContextTargetingMode)(-1);
        _cachedPassengerCount = -1;
        _cachedPassengerCapacity = -1;
        _cachedTransportId = default;
        Visible = false;
        _boardButton.Visible = false;
        _disembarkHereButton.Visible = false;
        _disembarkButton.Visible = false;
        _passengerLabel.Visible = false;
        _statusLabel.Visible = false;
    }

    private void OnBoardButtonPressed()
    {
        SetTargetingMode(TargetingMode == ContextTargetingMode.BoardTransport
            ? ContextTargetingMode.None
            : ContextTargetingMode.BoardTransport);
    }

    private void OnDisembarkButtonPressed()
    {
        SetTargetingMode(TargetingMode == ContextTargetingMode.Disembark
            ? ContextTargetingMode.None
            : ContextTargetingMode.Disembark);
    }

    public void SetTargetingMode(ContextTargetingMode mode)
    {
        if (TargetingMode == mode) return;
        TargetingMode = mode;
        _boardButton.SetPressedNoSignal(mode == ContextTargetingMode.BoardTransport);
        _disembarkButton.SetPressedNoSignal(mode == ContextTargetingMode.Disembark);
        TargetingModeChanged?.Invoke(mode);
    }

    public void CancelTargeting()
    {
        SetTargetingMode(ContextTargetingMode.None);
    }

    public void UpdateContext(
        HashSet<EntityId> selectedUnits,
        ClientReplica replica)
    {
        int count = selectedUnits.Count;
        if (count == 0)
        {
            if (TargetingMode != ContextTargetingMode.None)
            {
                CancelTargeting();
            }
            if (_cachedSelectionCount != 0)
            {
                _cachedSelectionCount = 0;
                _cachedSelectionHash = 0;
                _cachedTargetingMode = ContextTargetingMode.None;
                _cachedPassengerCount = -1;
                _cachedPassengerCapacity = -1;
                _cachedTransportId = default;
                Visible = false;
            }
            return;
        }

        uint hash = 17;
        bool allInfantry = true;
        bool hasTransport = false;
        OwnUnitObservation singleTransport = default;

        foreach (EntityId id in selectedUnits)
        {
            hash = unchecked(hash * 31 + id.Value);
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation obs))
            {
                allInfantry = false;
                continue;
            }

            string? classification = obs.Definition < _catalog.Units.Length
                ? _catalog.Units[obs.Definition].Classification
                : null;

            if (!string.Equals(classification, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                allInfantry = false;
            }

            if (obs.PassengerCapacity > 0)
            {
                hasTransport = true;
                if (count == 1)
                {
                    singleTransport = obs;
                }
            }
        }

        int passengerCount = hasTransport && count == 1 ? singleTransport.PassengerCount : -1;
        int passengerCapacity = hasTransport && count == 1 ? singleTransport.PassengerCapacity : -1;
        EntityId transportId = hasTransport && count == 1 ? singleTransport.Id : default;

        // Skip layout/string updates if unchanged
        if (_cachedSelectionCount == count &&
            _cachedSelectionHash == hash &&
            _cachedTargetingMode == TargetingMode &&
            _cachedPassengerCount == passengerCount &&
            _cachedPassengerCapacity == passengerCapacity &&
            _cachedTransportId == transportId)
        {
            return;
        }

        _cachedSelectionCount = count;
        _cachedSelectionHash = hash;
        _cachedTargetingMode = TargetingMode;
        _cachedPassengerCount = passengerCount;
        _cachedPassengerCapacity = passengerCapacity;
        _cachedTransportId = transportId;

        if (allInfantry && count > 0)
        {
            Visible = true;
            _boardButton.Visible = true;
            _boardButton.Text = TargetingMode == ContextTargetingMode.BoardTransport
                ? "CANCEL BOARDING"
                : "BOARD TRANSPORT";
            _disembarkHereButton.Visible = false;
            _disembarkButton.Visible = false;
            _passengerLabel.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.Text = TargetingMode == ContextTargetingMode.BoardTransport
                ? "Click friendly transport to board (Esc to cancel)"
                : (count == 1 ? "1 infantry selected" : $"{count} infantry selected");
        }
        else if (hasTransport && count == 1)
        {
            Visible = true;
            _boardButton.Visible = false;
            _passengerLabel.Visible = true;
            _passengerLabel.Text = $"Passengers: {singleTransport.PassengerCount}/{singleTransport.PassengerCapacity}";

            if (singleTransport.PassengerCount > 0)
            {
                _disembarkHereButton.Visible = true;
                _disembarkButton.Visible = true;
                _disembarkButton.Text = TargetingMode == ContextTargetingMode.Disembark
                    ? "CANCEL UNLOAD"
                    : "UNLOAD AT...";
                _statusLabel.Visible = true;
                _statusLabel.Text = TargetingMode == ContextTargetingMode.Disembark
                    ? "Click ground destination to unload at (Esc to cancel)"
                    : "Ready for disembarkation";
            }
            else
            {
                _disembarkHereButton.Visible = false;
                _disembarkButton.Visible = false;
                _statusLabel.Visible = true;
                _statusLabel.Text = "Empty transport";
            }
        }
        else
        {
            if (TargetingMode != ContextTargetingMode.None)
            {
                CancelTargeting();
            }
            Visible = false;
        }
    }
}
