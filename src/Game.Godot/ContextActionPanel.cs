using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

public enum ContextTargetingMode
{
    None = 0,
    BoardTransport = 1,
    Disembark = 2,
    LandAt = 3
}

/// <summary>
/// Contextual action panel displaying boarding, disembarking, smoke, and rotorcraft flight controls.
/// Zero heap allocations in steady-state frames with unchanged selection and unit state.
/// </summary>
internal sealed partial class ContextActionPanel : PanelContainer
{
    private readonly Button _boardButton;
    private readonly Button _disembarkHereButton;
    private readonly Button _disembarkButton;
    private readonly Button _smokeButton;
    private readonly Button _flightToggleButton;
    private readonly Button _landAtButton;
    private readonly Button _altitudeBandButton;
    private readonly Label _passengerLabel;
    private readonly Label _statusLabel;
    private UnitCatalog _catalog;

    public void SetCatalog(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cachedSelectionCount = -1;
    }

    public ContextTargetingMode TargetingMode { get; private set; } = ContextTargetingMode.None;

    internal Button BoardButton => _boardButton;
    internal Button DisembarkHereButton => _disembarkHereButton;
    internal Button DisembarkButton => _disembarkButton;
    internal Button SmokeButton => _smokeButton;
    internal Button FlightToggleButton => _flightToggleButton;
    internal Button LandAtButton => _landAtButton;
    internal Button AltitudeBandButton => _altitudeBandButton;
    internal Label PassengerLabel => _passengerLabel;
    internal Label StatusLabel => _statusLabel;

    public event Action<ContextTargetingMode>? TargetingModeChanged;
    public event Action? DisembarkHereRequested;
    public event Action? DeploySmokeRequested;
    public event Action? HelicopterToggleFlightRequested;
    public event Action? HelicopterToggleAltitudeRequested;

    // Cached state for zero-allocation steady-state updates
    private int _cachedSelectionCount = -1;
    private uint _cachedSelectionHash;
    private ContextTargetingMode _cachedTargetingMode = (ContextTargetingMode)(-1);
    private int _cachedPassengerCount = -1;
    private int _cachedPassengerCapacity = -1;
    private EntityId _cachedTransportId;
    private int _cachedSmokeCapableCount = -1;
    private int _cachedSmokeReadyCount = -1;
    private int _cachedSmokeActiveCount = -1;
    private int _cachedSmokeChargesTotal = -1;
    private int _cachedSmokeMinCooldownSec = -1;
    private int _cachedHeliCount = -1;
    private int _cachedHeliAirborneCount = -1;
    private int _cachedHeliLowBandCount = -1;
    private int _cachedHeliCrashingCount = -1;

    public ContextActionPanel(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(360, 48);

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.12f, 0.14f, 0.18f, 0.92f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.40f, 0.50f, 0.65f, 0.80f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        AddThemeStyleboxOverride("panel", style);

        HBoxContainer hbox = new()
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        hbox.AddThemeConstantOverride("separation", 10);
        AddChild(hbox);

        _boardButton = new Button
        {
            Text = "BOARD TRANSPORT",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(140, 32)
        };
        _boardButton.AddThemeFontSizeOverride("font_size", 11);
        _boardButton.Pressed += OnBoardButtonPressed;
        hbox.AddChild(_boardButton);

        _disembarkHereButton = new Button
        {
            Text = "DISEMBARK HERE",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(130, 32)
        };
        _disembarkHereButton.AddThemeFontSizeOverride("font_size", 11);
        _disembarkHereButton.Pressed += () => DisembarkHereRequested?.Invoke();
        hbox.AddChild(_disembarkHereButton);

        _disembarkButton = new Button
        {
            Text = "UNLOAD AT...",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(120, 32)
        };
        _disembarkButton.AddThemeFontSizeOverride("font_size", 11);
        _disembarkButton.Pressed += OnDisembarkButtonPressed;
        hbox.AddChild(_disembarkButton);

        _smokeButton = new Button
        {
            Text = "SMOKE [G]",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(130, 32)
        };
        _smokeButton.AddThemeFontSizeOverride("font_size", 11);
        _smokeButton.Pressed += () => DeploySmokeRequested?.Invoke();
        hbox.AddChild(_smokeButton);

        _flightToggleButton = new Button
        {
            Text = "TAKE OFF [L]",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(120, 32)
        };
        _flightToggleButton.AddThemeFontSizeOverride("font_size", 11);
        _flightToggleButton.Pressed += () => HelicopterToggleFlightRequested?.Invoke();
        hbox.AddChild(_flightToggleButton);

        _landAtButton = new Button
        {
            Text = "LAND AT...",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(105, 32)
        };
        _landAtButton.AddThemeFontSizeOverride("font_size", 11);
        _landAtButton.Pressed += OnLandAtButtonPressed;
        hbox.AddChild(_landAtButton);

        _altitudeBandButton = new Button
        {
            Text = "ALT: HIGH [H]",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(115, 32)
        };
        _altitudeBandButton.AddThemeFontSizeOverride("font_size", 11);
        _altitudeBandButton.Pressed += () => HelicopterToggleAltitudeRequested?.Invoke();
        hbox.AddChild(_altitudeBandButton);

        _passengerLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        _passengerLabel.AddThemeFontSizeOverride("font_size", 11);
        _passengerLabel.AddThemeColorOverride("font_color", new Color("edc76b"));
        hbox.AddChild(_passengerLabel);

        _statusLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 10);
        _statusLabel.AddThemeColorOverride("font_color", new Color("a0a8b0"));
        hbox.AddChild(_statusLabel);

        Visible = false;
        _boardButton.Visible = false;
        _disembarkHereButton.Visible = false;
        _disembarkButton.Visible = false;
        _smokeButton.Visible = false;
        _flightToggleButton.Visible = false;
        _landAtButton.Visible = false;
        _altitudeBandButton.Visible = false;
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

    private void OnLandAtButtonPressed()
    {
        SetTargetingMode(TargetingMode == ContextTargetingMode.LandAt
            ? ContextTargetingMode.None
            : ContextTargetingMode.LandAt);
    }

    public void SetTargetingMode(ContextTargetingMode mode)
    {
        if (TargetingMode == mode) return;
        TargetingMode = mode;
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
                _cachedSmokeCapableCount = -1;
                _cachedSmokeReadyCount = -1;
                _cachedSmokeActiveCount = -1;
                _cachedSmokeChargesTotal = -1;
                _cachedSmokeMinCooldownSec = -1;
                _cachedHeliCount = -1;
                _cachedHeliAirborneCount = -1;
                _cachedHeliLowBandCount = -1;
                _cachedHeliCrashingCount = -1;
                Visible = false;
            }
            return;
        }

        uint hash = 17;
        bool allInfantry = true;
        bool hasTransport = false;
        OwnUnitObservation singleTransport = default;
        int smokeCapableCount = 0;
        int smokeReadyCount = 0;
        int smokeActiveCount = 0;
        int smokeChargesTotal = 0;
        int smokeMinCooldownTicks = int.MaxValue;
        int heliCount = 0;
        int heliAirborneCount = 0;
        int heliLowBandCount = 0;
        int heliCrashingCount = 0;

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

            if (obs.HasFlightCapability)
            {
                heliCount++;
                if (obs.FlightPhase == FlightPhase.Crashing)
                {
                    heliCrashingCount++;
                }
                else if (obs.FlightPhase != FlightPhase.Ground)
                {
                    heliAirborneCount++;
                }
                if (obs.AltitudeBand == AltitudeBand.Low)
                {
                    heliLowBandCount++;
                }
            }

            if (obs.HasSmokeCapability)
            {
                smokeCapableCount++;
                smokeChargesTotal += obs.SmokeCharges;
                if (obs.SmokeActive)
                {
                    smokeActiveCount++;
                }
                else if (obs.SmokeCharges > 0 && obs.SmokeCooldownTicks == 0)
                {
                    smokeReadyCount++;
                }
                else if (obs.SmokeCharges > 0 && obs.SmokeCooldownTicks > 0 && obs.SmokeCooldownTicks < smokeMinCooldownTicks)
                {
                    smokeMinCooldownTicks = obs.SmokeCooldownTicks;
                }
            }
        }

        int passengerCount = hasTransport && count == 1 ? singleTransport.PassengerCount : -1;
        int passengerCapacity = hasTransport && count == 1 ? singleTransport.PassengerCapacity : -1;
        EntityId transportId = hasTransport && count == 1 ? singleTransport.Id : default;
        int smokeMinCooldownSec = smokeMinCooldownTicks < int.MaxValue
            ? (int)MathF.Ceiling(smokeMinCooldownTicks * Simulation.TickSeconds)
            : 0;

        // Skip layout/string updates if unchanged
        if (_cachedSelectionCount == count &&
            _cachedSelectionHash == hash &&
            _cachedTargetingMode == TargetingMode &&
            _cachedPassengerCount == passengerCount &&
            _cachedPassengerCapacity == passengerCapacity &&
            _cachedTransportId == transportId &&
            _cachedSmokeCapableCount == smokeCapableCount &&
            _cachedSmokeReadyCount == smokeReadyCount &&
            _cachedSmokeActiveCount == smokeActiveCount &&
            _cachedSmokeChargesTotal == smokeChargesTotal &&
            _cachedSmokeMinCooldownSec == smokeMinCooldownSec &&
            _cachedHeliCount == heliCount &&
            _cachedHeliAirborneCount == heliAirborneCount &&
            _cachedHeliLowBandCount == heliLowBandCount &&
            _cachedHeliCrashingCount == heliCrashingCount)
        {
            return;
        }

        _cachedSelectionCount = count;
        _cachedSelectionHash = hash;
        _cachedTargetingMode = TargetingMode;
        _cachedPassengerCount = passengerCount;
        _cachedPassengerCapacity = passengerCapacity;
        _cachedTransportId = transportId;
        _cachedSmokeCapableCount = smokeCapableCount;
        _cachedSmokeReadyCount = smokeReadyCount;
        _cachedSmokeActiveCount = smokeActiveCount;
        _cachedSmokeChargesTotal = smokeChargesTotal;
        _cachedSmokeMinCooldownSec = smokeMinCooldownSec;
        _cachedHeliCount = heliCount;
        _cachedHeliAirborneCount = heliAirborneCount;
        _cachedHeliLowBandCount = heliLowBandCount;
        _cachedHeliCrashingCount = heliCrashingCount;

        bool hasSmokeUi = smokeCapableCount > 0;
        _smokeButton.Visible = hasSmokeUi;
        if (hasSmokeUi)
        {
            if (smokeReadyCount > 0)
            {
                _smokeButton.Disabled = false;
                _smokeButton.Text = $"SMOKE [G] ({smokeChargesTotal})";
            }
            else if (smokeActiveCount > 0)
            {
                _smokeButton.Disabled = true;
                _smokeButton.Text = "LAYING SMOKE...";
            }
            else if (smokeChargesTotal > 0 && smokeMinCooldownSec > 0)
            {
                _smokeButton.Disabled = true;
                _smokeButton.Text = $"SMOKE CD {smokeMinCooldownSec}s";
            }
            else
            {
                _smokeButton.Disabled = true;
                _smokeButton.Text = "SMOKE (0)";
            }
        }

        bool hasHeliUi = heliCount > 0;
        bool allCrashing = hasHeliUi && heliCrashingCount == heliCount;
        _flightToggleButton.Visible = hasHeliUi;
        _landAtButton.Visible = hasHeliUi;
        _altitudeBandButton.Visible = hasHeliUi;
        if (hasHeliUi)
        {
            _flightToggleButton.Disabled = allCrashing;
            _landAtButton.Disabled = allCrashing;
            _altitudeBandButton.Disabled = allCrashing;
            _flightToggleButton.Text = heliAirborneCount > 0 ? "LAND HERE [L]" : "TAKE OFF [L]";
            _landAtButton.Text = TargetingMode == ContextTargetingMode.LandAt ? "CANCEL LAND" : "LAND AT...";
            _altitudeBandButton.Text = heliLowBandCount > 0 ? "ALT: LOW [H]" : "ALT: HIGH [H]";
        }

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
                _statusLabel.Text = TargetingMode == ContextTargetingMode.LandAt
                    ? "Click clear terrain to land (Esc to cancel)"
                    : TargetingMode == ContextTargetingMode.Disembark
                        ? "Click ground destination to unload at (Esc to cancel)"
                        : hasHeliUi
                            ? (allCrashing ? "Mayday - crashing" : (heliAirborneCount > 0 ? "Airborne - land to unload" : "Landed - ready for disembarkation"))
                            : "Ready for disembarkation";
            }
            else
            {
                _disembarkHereButton.Visible = false;
                _disembarkButton.Visible = false;
                _statusLabel.Visible = true;
                _statusLabel.Text = TargetingMode == ContextTargetingMode.LandAt
                    ? "Click clear terrain to land (Esc to cancel)"
                    : hasHeliUi
                        ? (allCrashing ? "Mayday - crashing" : (heliAirborneCount > 0 ? "Airborne hover / transit" : "Landed on LZ"))
                        : "Empty transport";
            }
        }
        else if (hasHeliUi)
        {
            if (TargetingMode != ContextTargetingMode.None && TargetingMode != ContextTargetingMode.LandAt)
            {
                CancelTargeting();
            }
            Visible = true;
            _boardButton.Visible = false;
            _disembarkHereButton.Visible = false;
            _disembarkButton.Visible = false;
            _passengerLabel.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.Text = TargetingMode == ContextTargetingMode.LandAt
                ? "Click clear terrain to land (Esc to cancel)"
                : allCrashing
                    ? "Mayday - crashing"
                    : heliAirborneCount > 0
                        ? "Airborne rotorcraft (L: land, H: altitude)"
                        : "Landed rotorcraft (L: take off, H: altitude)";
        }
        else if (hasSmokeUi)
        {
            if (TargetingMode != ContextTargetingMode.None)
            {
                CancelTargeting();
            }
            Visible = true;
            _boardButton.Visible = false;
            _disembarkHereButton.Visible = false;
            _disembarkButton.Visible = false;
            _passengerLabel.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.Text = smokeActiveCount > 0
                ? "Smoke screen active (blocks sight)"
                : smokeReadyCount > 0
                    ? "Press G to lay smoke screen"
                    : smokeChargesTotal > 0
                        ? $"Smoke recharging ({smokeMinCooldownSec}s)"
                        : "Smoke charges depleted";
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
