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
    LandAt = 3,
    FastRope = 4,
    SlingPickup = 5,
    SlingDrop = 6,
    FireMission = 7
}

/// <summary>
/// Contextual action panel displaying boarding, disembarking, smoke, and rotorcraft flight/cargo controls.
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
    private readonly Button _fastRopeButton;
    private readonly Button _slingButton;
    private readonly Button _fireMissionButton;
    private readonly Label _passengerLabel;
    private readonly Label _statusLabel;
    private UnitCatalog _catalog;

    public ContextTargetingMode TargetingMode { get; private set; } = ContextTargetingMode.None;

    internal Button BoardButton => _boardButton;
    internal Button DisembarkHereButton => _disembarkHereButton;
    internal Button DisembarkButton => _disembarkButton;
    internal Button SmokeButton => _smokeButton;
    internal Button FlightToggleButton => _flightToggleButton;
    internal Button LandAtButton => _landAtButton;
    internal Button AltitudeBandButton => _altitudeBandButton;
    internal Button FastRopeButton => _fastRopeButton;
    internal Button SlingButton => _slingButton;
    internal Button FireMissionButton => _fireMissionButton;
    internal Label PassengerLabel => _passengerLabel;
    internal Label StatusLabel => _statusLabel;

    public event Action<ContextTargetingMode>? TargetingModeChanged;
    public event Action? DisembarkHereRequested;
    public event Action? DeploySmokeRequested;
    public event Action? HelicopterToggleFlightRequested;
    public event Action? HelicopterToggleAltitudeRequested;
    public event Action? CancelFireMissionRequested;

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
    private int _cachedHeliFastRopeCapable = -1;
    private int _cachedHeliFastRopeActive = -1;
    private int _cachedHeliSlingCapable = -1;
    private int _cachedHeliSlingLoaded = -1;
    private int _cachedArtilleryCount = -1;
    private int _cachedArtilleryActiveCount = -1;
    private int _cachedArtillerySettingUpCount = -1;
    private int _cachedArtilleryAmmoTotal = -1;
    private int _cachedArtilleryMinReloadSec = -1;
    private bool _cachedAllBoardable;

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

        _smokeButton = new Button
        {
            Text = "SMOKE [G]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(86, 22)
        };
        _smokeButton.Pressed += () => DeploySmokeRequested?.Invoke();
        hbox.AddChild(_smokeButton);

        _flightToggleButton = new Button
        {
            Text = "TAKE OFF [L]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(86, 22)
        };
        _flightToggleButton.Pressed += () => HelicopterToggleFlightRequested?.Invoke();
        hbox.AddChild(_flightToggleButton);

        _landAtButton = new Button
        {
            Text = "LAND AT...",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(78, 22)
        };
        _landAtButton.Pressed += OnLandAtButtonPressed;
        hbox.AddChild(_landAtButton);

        _altitudeBandButton = new Button
        {
            Text = "ALT: HIGH [H]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(86, 22)
        };
        _altitudeBandButton.Pressed += () => HelicopterToggleAltitudeRequested?.Invoke();
        hbox.AddChild(_altitudeBandButton);

        _fastRopeButton = new Button
        {
            Text = "FAST-ROPE [F]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(88, 22)
        };
        _fastRopeButton.Pressed += OnFastRopeButtonPressed;
        hbox.AddChild(_fastRopeButton);

        _slingButton = new Button
        {
            Text = "SLING PICKUP [K]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(96, 22)
        };
        _slingButton.Pressed += OnSlingButtonPressed;
        hbox.AddChild(_slingButton);

        _fireMissionButton = new Button
        {
            Text = "FIRE BARRAGE [B]",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(106, 22)
        };
        _fireMissionButton.Pressed += OnFireMissionButtonPressed;
        hbox.AddChild(_fireMissionButton);

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
        _smokeButton.Visible = false;
        _flightToggleButton.Visible = false;
        _landAtButton.Visible = false;
        _altitudeBandButton.Visible = false;
        _fastRopeButton.Visible = false;
        _slingButton.Visible = false;
        _fireMissionButton.Visible = false;
        _passengerLabel.Visible = false;
        _statusLabel.Visible = false;
    }

    public void SetCatalog(UnitCatalog catalog)
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
        _smokeButton.Visible = false;
        _flightToggleButton.Visible = false;
        _landAtButton.Visible = false;
        _altitudeBandButton.Visible = false;
        _fastRopeButton.Visible = false;
        _slingButton.Visible = false;
        _fireMissionButton.Visible = false;
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

    private void OnFastRopeButtonPressed()
    {
        SetTargetingMode(TargetingMode == ContextTargetingMode.FastRope
            ? ContextTargetingMode.None
            : ContextTargetingMode.FastRope);
    }

    private void OnSlingButtonPressed()
    {
        ContextTargetingMode desired = _cachedHeliSlingLoaded > 0
            ? ContextTargetingMode.SlingDrop
            : ContextTargetingMode.SlingPickup;
        SetTargetingMode(TargetingMode == desired ? ContextTargetingMode.None : desired);
    }

    private void OnFireMissionButtonPressed()
    {
        if (_cachedArtilleryActiveCount > 0 && TargetingMode != ContextTargetingMode.FireMission)
        {
            CancelFireMissionRequested?.Invoke();
            return;
        }
        SetTargetingMode(TargetingMode == ContextTargetingMode.FireMission
            ? ContextTargetingMode.None
            : ContextTargetingMode.FireMission);
    }

    private WeaponData? FindWeaponData(string? weaponId)
    {
        if (weaponId is null || _catalog.Weapons is null) return null;
        for (int i = 0; i < _catalog.Weapons.Length; i++)
        {
            if (string.Equals(_catalog.Weapons[i].Id, weaponId, StringComparison.Ordinal))
                return _catalog.Weapons[i];
        }
        return null;
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
                _cachedSmokeCapableCount = -1;
                _cachedSmokeReadyCount = -1;
                _cachedSmokeActiveCount = -1;
                _cachedSmokeChargesTotal = -1;
                _cachedSmokeMinCooldownSec = -1;
                _cachedHeliCount = -1;
                _cachedHeliAirborneCount = -1;
                _cachedHeliLowBandCount = -1;
                _cachedHeliCrashingCount = -1;
                _cachedHeliFastRopeCapable = -1;
                _cachedHeliFastRopeActive = -1;
                _cachedHeliSlingCapable = -1;
                _cachedHeliSlingLoaded = -1;
                _cachedArtilleryCount = -1;
                _cachedArtilleryActiveCount = -1;
                _cachedArtillerySettingUpCount = -1;
                _cachedArtilleryAmmoTotal = -1;
                _cachedArtilleryMinReloadSec = -1;
                _cachedAllBoardable = false;
                Visible = false;
            }
            return;
        }

        uint hash = 17;
        bool allBoardable = true;
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
        int heliFastRopeCapable = 0;
        int heliFastRopeActive = 0;
        int heliSlingCapable = 0;
        int heliSlingLoaded = 0;
        int artilleryCount = 0;
        int artilleryActiveCount = 0;
        int artillerySettingUpCount = 0;
        int artilleryAmmoTotal = 0;
        int artilleryMinReloadTicks = int.MaxValue;

        foreach (EntityId id in selectedUnits)
        {
            hash = unchecked(hash * 31 + id.Value);
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation obs))
            {
                allInfantry = false;
                allBoardable = false;
                continue;
            }

            UnitDefinition? def = obs.Definition < _catalog.Units.Length
                ? _catalog.Units[obs.Definition]
                : null;
            string? classification = def?.Classification;
            bool isInfantry = string.Equals(classification, "infantry", StringComparison.OrdinalIgnoreCase);
            bool canBoard = isInfantry || (def?.CanInternalLoad == true);
            if (!isInfantry)
            {
                allInfantry = false;
            }
            if (!canBoard || obs.SlungById != default)
            {
                allBoardable = false;
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
                if (def?.Flight?.CanFastRope == true)
                {
                    heliFastRopeCapable++;
                }
                if ((obs.FlightFlags & FlightStateFlags.FastRopeActive) != 0)
                {
                    heliFastRopeActive++;
                }
                if (def?.Flight?.MaxSlingWeightKg > 0f)
                {
                    heliSlingCapable++;
                }
                if (obs.SlingLoadId != default)
                {
                    heliSlingLoaded++;
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

            WeaponData? weaponData = FindWeaponData(def?.Weapon);
            if (weaponData is not null && weaponData.IsIndirectFire)
            {
                artilleryCount++;
                artilleryAmmoTotal += obs.Ammunition;
                if (obs.ArtilleryFlags != ArtilleryFlags.None)
                {
                    artilleryActiveCount++;
                    if ((obs.ArtilleryFlags & ArtilleryFlags.SettingUp) != 0)
                        artillerySettingUpCount++;
                }
                if (obs.Ammunition > 0 && obs.ReloadTicksRemaining > 0 && obs.ReloadTicksRemaining < artilleryMinReloadTicks)
                {
                    artilleryMinReloadTicks = obs.ReloadTicksRemaining;
                }
            }
        }

        int passengerCount = hasTransport && count == 1 ? singleTransport.PassengerCount : -1;
        int passengerCapacity = hasTransport && count == 1 ? singleTransport.PassengerCapacity : -1;
        EntityId transportId = hasTransport && count == 1 ? singleTransport.Id : default;
        int smokeMinCooldownSec = smokeMinCooldownTicks < int.MaxValue
            ? (int)MathF.Ceiling(smokeMinCooldownTicks * Simulation.TickSeconds)
            : 0;
        int artilleryMinReloadSec = artilleryMinReloadTicks < int.MaxValue
            ? (int)MathF.Ceiling(artilleryMinReloadTicks * Simulation.TickSeconds)
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
            _cachedHeliCrashingCount == heliCrashingCount &&
            _cachedHeliFastRopeCapable == heliFastRopeCapable &&
            _cachedHeliFastRopeActive == heliFastRopeActive &&
            _cachedHeliSlingCapable == heliSlingCapable &&
            _cachedHeliSlingLoaded == heliSlingLoaded &&
            _cachedArtilleryCount == artilleryCount &&
            _cachedArtilleryActiveCount == artilleryActiveCount &&
            _cachedArtillerySettingUpCount == artillerySettingUpCount &&
            _cachedArtilleryAmmoTotal == artilleryAmmoTotal &&
            _cachedArtilleryMinReloadSec == artilleryMinReloadSec &&
            _cachedAllBoardable == allBoardable)
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
        _cachedHeliFastRopeCapable = heliFastRopeCapable;
        _cachedHeliFastRopeActive = heliFastRopeActive;
        _cachedHeliSlingCapable = heliSlingCapable;
        _cachedHeliSlingLoaded = heliSlingLoaded;
        _cachedArtilleryCount = artilleryCount;
        _cachedArtilleryActiveCount = artilleryActiveCount;
        _cachedArtillerySettingUpCount = artillerySettingUpCount;
        _cachedArtilleryAmmoTotal = artilleryAmmoTotal;
        _cachedArtilleryMinReloadSec = artilleryMinReloadSec;
        _cachedAllBoardable = allBoardable;

        bool hasArtilleryUi = artilleryCount > 0;
        _fireMissionButton.Visible = hasArtilleryUi;
        if (hasArtilleryUi)
        {
            if (TargetingMode == ContextTargetingMode.FireMission)
            {
                _fireMissionButton.Disabled = false;
                _fireMissionButton.Text = "CANCEL TARGET [Esc]";
            }
            else if (artilleryActiveCount > 0)
            {
                _fireMissionButton.Disabled = false;
                _fireMissionButton.Text = "STOP BARRAGE [B]";
            }
            else if (artilleryAmmoTotal <= 0)
            {
                _fireMissionButton.Disabled = true;
                _fireMissionButton.Text = "NO ROCKETS (0)";
            }
            else
            {
                _fireMissionButton.Disabled = false;
                _fireMissionButton.Text = $"FIRE BARRAGE [B] ({artilleryAmmoTotal})";
            }
        }

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
        bool showFastRope = hasHeliUi && heliFastRopeCapable > 0 && (passengerCount > 0 || count > 1);
        bool showSling = hasHeliUi && heliSlingCapable > 0;
        _fastRopeButton.Visible = showFastRope;
        _slingButton.Visible = showSling;
        if (hasHeliUi)
        {
            _flightToggleButton.Disabled = allCrashing;
            _landAtButton.Disabled = allCrashing;
            _altitudeBandButton.Disabled = allCrashing;
            _flightToggleButton.Text = heliAirborneCount > 0 ? "LAND HERE [L]" : "TAKE OFF [L]";
            _landAtButton.Text = TargetingMode == ContextTargetingMode.LandAt ? "CANCEL LAND" : "LAND AT...";
            _altitudeBandButton.Text = heliLowBandCount > 0 ? "ALT: LOW [H]" : "ALT: HIGH [H]";
            if (showFastRope)
            {
                _fastRopeButton.Disabled = allCrashing;
                _fastRopeButton.Text = TargetingMode == ContextTargetingMode.FastRope
                    ? "CANCEL ROPE"
                    : (heliFastRopeActive > 0 ? "ROPING..." : "FAST-ROPE [F]");
            }
            if (showSling)
            {
                _slingButton.Disabled = allCrashing;
                if (heliSlingLoaded > 0)
                {
                    _slingButton.Text = TargetingMode == ContextTargetingMode.SlingDrop ? "CANCEL DROP" : "DROP SLING [K]";
                }
                else
                {
                    _slingButton.Text = TargetingMode == ContextTargetingMode.SlingPickup ? "CANCEL SLING" : "SLING PICKUP [K]";
                }
            }
        }

        if (allBoardable && count > 0)
        {
            Visible = true;
            _boardButton.Visible = true;
            _boardButton.Text = TargetingMode == ContextTargetingMode.BoardTransport
                ? "CANCEL BOARDING"
                : (allInfantry ? "BOARD TRANSPORT" : "LOAD RAMP");
            _disembarkHereButton.Visible = false;
            _disembarkButton.Visible = false;
            _passengerLabel.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.Text = TargetingMode == ContextTargetingMode.BoardTransport
                ? (allInfantry
                    ? "Click friendly transport to board (Esc to cancel)"
                    : "Click landed cargo helicopter to drive up ramp (Esc to cancel)")
                : (allInfantry
                    ? (count == 1 ? "1 infantry selected" : $"{count} infantry selected")
                    : "Light cargo vehicle (can load ramp)");
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
                    : TargetingMode == ContextTargetingMode.FastRope
                        ? "Click drop zone for fast-rope insertion (forests OK, Esc to cancel)"
                        : TargetingMode == ContextTargetingMode.SlingPickup
                            ? "Click friendly light/APC vehicle to sling-load (Esc to cancel)"
                            : TargetingMode == ContextTargetingMode.SlingDrop
                                ? "Click clear ground to release sling cargo (Esc to cancel)"
                                : TargetingMode == ContextTargetingMode.Disembark
                                    ? "Click ground destination to unload at (Esc to cancel)"
                                    : hasHeliUi
                                        ? (allCrashing ? "Mayday - crashing"
                                            : heliFastRopeActive > 0 ? "Fast-roping troops"
                                            : heliSlingLoaded > 0 ? "Carrying sling cargo"
                                            : (heliAirborneCount > 0 ? "Airborne - land or fast-rope" : "Landed - ready for disembarkation"))
                                        : "Ready for disembarkation";
            }
            else
            {
                _disembarkHereButton.Visible = false;
                _disembarkButton.Visible = false;
                _statusLabel.Visible = true;
                _statusLabel.Text = TargetingMode == ContextTargetingMode.LandAt
                    ? "Click clear terrain to land (Esc to cancel)"
                    : TargetingMode == ContextTargetingMode.SlingPickup
                        ? "Click friendly light/APC vehicle to sling-load (Esc to cancel)"
                        : TargetingMode == ContextTargetingMode.SlingDrop
                            ? "Click clear ground to release sling cargo (Esc to cancel)"
                            : hasHeliUi
                                ? (allCrashing ? "Mayday - crashing"
                                    : heliSlingLoaded > 0 ? "Carrying external sling cargo"
                                    : (heliAirborneCount > 0 ? "Airborne hover / transit" : "Landed on LZ"))
                                : "Empty transport";
            }
        }
        else if (hasHeliUi)
        {
            if (TargetingMode != ContextTargetingMode.None &&
                TargetingMode != ContextTargetingMode.LandAt &&
                TargetingMode != ContextTargetingMode.FastRope &&
                TargetingMode != ContextTargetingMode.SlingPickup &&
                TargetingMode != ContextTargetingMode.SlingDrop)
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
                : TargetingMode == ContextTargetingMode.FastRope
                    ? "Click drop zone for fast-rope insertion (forests OK, Esc to cancel)"
                    : TargetingMode == ContextTargetingMode.SlingPickup
                        ? "Click friendly vehicle to sling-load (Esc to cancel)"
                        : TargetingMode == ContextTargetingMode.SlingDrop
                            ? "Click clear ground to release sling cargo (Esc to cancel)"
                            : allCrashing
                                ? "Mayday - crashing"
                                : heliAirborneCount > 0
                                    ? "Airborne rotorcraft (L: land, H: alt, F: rope, K: sling)"
                                    : "Landed rotorcraft (L: take off, H: alt, K: sling)";
        }
        else if (hasArtilleryUi)
        {
            if (TargetingMode != ContextTargetingMode.None &&
                TargetingMode != ContextTargetingMode.FireMission)
            {
                CancelTargeting();
            }
            Visible = true;
            _boardButton.Visible = false;
            _disembarkHereButton.Visible = false;
            _disembarkButton.Visible = false;
            _passengerLabel.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.Text = TargetingMode == ContextTargetingMode.FireMission
                ? "Click target area for rocket barrage (Esc to cancel)"
                : artillerySettingUpCount > 0
                    ? "Elevating launcher rack - preparing barrage..."
                    : artilleryActiveCount > 0
                        ? $"Firing rocket salvo ({artilleryAmmoTotal} rockets left)"
                        : artilleryAmmoTotal <= 0
                            ? "Rockets depleted (0 remaining)"
                            : artilleryMinReloadSec > 0
                                ? $"Reloading salvo ({artilleryMinReloadSec}s) - B to queue target"
                                : "Rocket artillery ready (press B to target barrage)";
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
