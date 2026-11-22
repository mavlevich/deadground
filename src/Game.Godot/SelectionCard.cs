using System.Globalization;
using System.Text;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

// Displays persistent tactical selection details for own units, groups, and enemy contacts.
internal sealed partial class SelectionCard : PanelContainer
{
    private const int MaxRosterChips = 8;

    private static readonly StringName FontColorName = new("font_color");
    private static readonly StyleBoxFlat NormalPanelStyle = HudTheme.CreatePanel(padding: 8, topRail: true);
    private static readonly StyleBoxFlat DangerPanelStyle = HudTheme.CreatePanel(padding: 8, danger: true, topRail: true);

    private static void SetLabelColor(Label label, Color color) => label.AddThemeColorOverride(FontColorName, color);

    private readonly UnitRoleSymbol _headerSymbol;
    private readonly Label _titleLabel;
    private readonly Label _roleTagLabel;
    private readonly Label _line1Label;
    private readonly Label _readinessLabel;
    private readonly UnitReadinessBars _bars;
    private readonly Label _line2Label;
    private readonly Label _line3Label;
    private readonly Label _specsLabel;
    private readonly GridContainer _rosterGrid;
    private readonly Button[] _rosterChips = new Button[MaxRosterChips];
    private readonly EntityId[] _rosterChipIds = new EntityId[MaxRosterChips];
    private readonly StringBuilder _compositionBuilder = new(64);
    private UnitCatalog _catalog;
    private bool _dangerPanelActive;

    public event Action<EntityId, bool>? RosterUnitClicked;

    internal bool ReadinessVisible => _bars.Visible;
    internal WeaponReadinessPresentation Readiness => _bars.Readiness;
    internal string ReadinessText => _readinessLabel.Text;
    internal string SpecsText => _specsLabel.Text;
    internal int VisibleRosterChipCount { get; private set; }
    internal float PreferredHeight => _mode switch
    {
        DisplayMode.SingleUnit => HudLayout.CardHeight,
        DisplayMode.Contact => HudLayout.ContactCardHeight,
        _ => HudLayout.GroupCardHeight
    };

    public string TitleText => _titleLabel.Text;
    public string Line1Text => _line1Label.Text;
    public string Line2Text => _line2Label.Text;
    public string Line3Text => _line3Label.Text;

    public SelectionCard(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(HudLayout.CardWidth, HudLayout.CardHeight);
        AddThemeStyleboxOverride("panel", NormalPanelStyle);

        VBoxContainer vbox = new() { MouseFilter = MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 2);
        AddChild(vbox);

        // Row 1: Symbol + Title (left) | Role / Status tag (right)
        HBoxContainer titleRow = new() { MouseFilter = MouseFilterEnum.Ignore };
        titleRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(titleRow);

        _headerSymbol = new UnitRoleSymbol { Visible = false };
        titleRow.AddChild(_headerSymbol);

        _titleLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 13);
        SetLabelColor(_titleLabel, HudTheme.Text);
        titleRow.AddChild(_titleLabel);

        _roleTagLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        HudTheme.ApplyMono(_roleTagLabel);
        _roleTagLabel.AddThemeFontSizeOverride("font_size", 10);
        SetLabelColor(_roleTagLabel, HudTheme.Muted);
        titleRow.AddChild(_roleTagLabel);

        // Row 2: HP / state (left) | Readiness label (right)
        HBoxContainer statusRow = new() { MouseFilter = MouseFilterEnum.Ignore };
        statusRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(statusRow);

        _line1Label = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        HudTheme.ApplyMono(_line1Label);
        _line1Label.AddThemeFontSizeOverride("font_size", 10);
        statusRow.AddChild(_line1Label);

        _readinessLabel = new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        HudTheme.ApplyMono(_readinessLabel);
        _readinessLabel.AddThemeFontSizeOverride("font_size", 10);
        SetLabelColor(_readinessLabel, HudTheme.Accent);
        statusRow.AddChild(_readinessLabel);

        // Row 3: Segmented HP + Reload bars (single own unit)
        _bars = new UnitReadinessBars(compact: false) { Visible = false };
        vbox.AddChild(_bars);

        // Row 3b: Clickable group unit roster chips (multiple own units)
        _rosterGrid = new GridContainer
        {
            Columns = 4,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _rosterGrid.AddThemeConstantOverride("h_separation", 4);
        _rosterGrid.AddThemeConstantOverride("v_separation", 3);
        for (int i = 0; i < MaxRosterChips; i++)
        {
            int slot = i;
            Button chip = new()
            {
                ThemeTypeVariation = "CompactButton",
                Visible = false,
                FocusMode = FocusModeEnum.None,
                MouseFilter = MouseFilterEnum.Stop,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                CustomMinimumSize = new Vector2(0, 20)
            };
            chip.AddThemeFontSizeOverride("font_size", 9);
            chip.Pressed += () => OnRosterChipPressed(slot);
            _rosterChips[i] = chip;
            _rosterGrid.AddChild(chip);
        }
        vbox.AddChild(_rosterGrid);

        // Row 4: Armament & ammo (left) | Stance & order status (right)
        HBoxContainer detailRow = new() { MouseFilter = MouseFilterEnum.Ignore };
        detailRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(detailRow);

        _line2Label = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        HudTheme.ApplyMono(_line2Label);
        _line2Label.AddThemeFontSizeOverride("font_size", 10);
        detailRow.AddChild(_line2Label);

        _line3Label = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _line3Label.AddThemeFontSizeOverride("font_size", 10);
        SetLabelColor(_line3Label, HudTheme.Muted);
        detailRow.AddChild(_line3Label);

        // Row 5: Tactical specs / catalog reference / group alerts
        _specsLabel = new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Pass,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        HudTheme.ApplyMono(_specsLabel);
        _specsLabel.AddThemeFontSizeOverride("font_size", 9);
        SetLabelColor(_specsLabel, HudTheme.Muted);
        vbox.AddChild(_specsLabel);

        UpdateEmpty();
    }

    internal void SetCatalog(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _mode = DisplayMode.None;
        UpdateEmpty();
    }

    private enum DisplayMode { None, Empty, SingleUnit, MultipleUnits, Contact }
    private DisplayMode _mode = DisplayMode.None;

    private void SetPanelStyle(bool danger)
    {
        if (_dangerPanelActive == danger) return;
        _dangerPanelActive = danger;
        AddThemeStyleboxOverride("panel", danger ? DangerPanelStyle : NormalPanelStyle);
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _titleLabel.TooltipText = string.Empty;
        _line1Label.TooltipText = string.Empty;
        _line2Label.TooltipText = string.Empty;
        _line3Label.TooltipText = string.Empty;
        _specsLabel.TooltipText = string.Empty;
        CustomMinimumSize = new Vector2(HudLayout.CardWidth, PreferredHeight);
        SetPanelStyle(mode == DisplayMode.Contact);
        _headerSymbol.Visible = mode is DisplayMode.SingleUnit or DisplayMode.Contact;
        _rosterGrid.Visible = mode == DisplayMode.MultipleUnits;
        _line2Label.Visible = mode != DisplayMode.MultipleUnits;
        _line3Label.Visible = mode != DisplayMode.MultipleUnits;
        _line3Label.HorizontalAlignment = mode == DisplayMode.Contact ? HorizontalAlignment.Right : HorizontalAlignment.Right;
    }

    // Single unit cached state
    private EntityId _cachedSingleId;
    private ushort _cachedSingleDef;
    private ushort _cachedSingleHealth;
    private ushort _cachedSingleAmmo;
    private ushort _cachedSingleReloadTicks;
    private AttackExecutionStatus _cachedSingleAttackStatus;
    private ContactId _cachedSingleTargetContact;
    private FireStance _cachedSingleStance;
    private PathRequestStatus _cachedSingleNavStatus;
    private byte _cachedSinglePassengers;
    private byte _cachedSingleCapacity;
    private int _cachedSingleControlGroup = -1;
    private FlightPhase _cachedSingleFlightPhase;
    private AltitudeBand _cachedSingleAltitudeBand;
    private int _cachedSingleAltMeters = -1;
    private bool _cachedSingleHasFlight;
    private EntityId _cachedSingleSlingLoadId;
    private EntityId _cachedSingleSlungById;
    private FlightStateFlags _cachedSingleFlightFlags;
    private byte _cachedSingleFlareCharges;
    private int _cachedSingleFlareCooldownSec = -1;
    private CountermeasureFlags _cachedSingleCmFlags;

    // Multiple units cached state
    private int _cachedMultiTotal = -1;
    private int _cachedMultiTanks = -1;
    private int _cachedMultiRecons = -1;
    private int _cachedMultiApcs = -1;
    private int _cachedMultiInfantry = -1;
    private int _cachedMultiCommands = -1;
    private int _cachedMultiOthers = -1;
    private int _cachedMultiDamaged = -1;
    private int _cachedMultiEmpty = -1;
    private int _cachedMultiGroup = -1;
    private uint _cachedRosterStateHash;

    // Contact cached state
    private ContactId _cachedContactId;
    private ContactState _cachedContactState;
    private ushort _cachedContactType = ushort.MaxValue;
    private ContactClassification _cachedContactClassification;
    private int _cachedContactAgeKey = -1;

    public void UpdateEmpty()
    {
        if (_mode == DisplayMode.Empty) return;
        SetDisplayMode(DisplayMode.Empty);
        _bars.Visible = false;
        _readinessLabel.Visible = false;
        _specsLabel.Visible = false;
        VisibleRosterChipCount = 0;

        _titleLabel.Text = "No Selection";
        SetLabelColor(_titleLabel, HudTheme.Muted);
        _roleTagLabel.Text = string.Empty;
        _line1Label.Text = "Left-click a friendly unit or enemy contact to inspect.";
        _line2Label.Text = string.Empty;
        _line3Label.Text = string.Empty;
    }

    public void UpdateSingleOwnUnit(OwnUnitObservation ownUnit, int controlGroup = 0)
    {
        int altMeters = ownUnit.HasFlightCapability ? (int)MathF.Round(ownUnit.Altitude) : 0;
        int flareCooldownSec = ownUnit.FlareCooldownTicks > 0
            ? (int)MathF.Ceiling(ownUnit.FlareCooldownTicks * Simulation.TickSeconds)
            : 0;
        if (_mode == DisplayMode.SingleUnit &&
            _cachedSingleId == ownUnit.Id &&
            _cachedSingleDef == ownUnit.Definition &&
            _cachedSingleHealth == ownUnit.Health &&
            _cachedSingleAmmo == ownUnit.Ammunition &&
            _cachedSingleReloadTicks == ownUnit.ReloadTicksRemaining &&
            _cachedSingleAttackStatus == ownUnit.AttackStatus &&
            _cachedSingleTargetContact == ownUnit.TargetContactId &&
            _cachedSingleStance == ownUnit.Stance &&
            _cachedSingleNavStatus == ownUnit.NavStatus &&
            _cachedSinglePassengers == ownUnit.PassengerCount &&
            _cachedSingleCapacity == ownUnit.PassengerCapacity &&
            _cachedSingleControlGroup == controlGroup &&
            _cachedSingleHasFlight == ownUnit.HasFlightCapability &&
            _cachedSingleFlightPhase == ownUnit.FlightPhase &&
            _cachedSingleAltitudeBand == ownUnit.AltitudeBand &&
            _cachedSingleAltMeters == altMeters &&
            _cachedSingleSlingLoadId == ownUnit.SlingLoadId &&
            _cachedSingleSlungById == ownUnit.SlungById &&
            _cachedSingleFlightFlags == ownUnit.FlightFlags &&
            _cachedSingleFlareCharges == ownUnit.FlareCharges &&
            _cachedSingleFlareCooldownSec == flareCooldownSec &&
            _cachedSingleCmFlags == ownUnit.CountermeasureFlags)
        {
            return;
        }

        SetDisplayMode(DisplayMode.SingleUnit);
        _cachedSingleId = ownUnit.Id;
        _cachedSingleDef = ownUnit.Definition;
        _cachedSingleHealth = ownUnit.Health;
        _cachedSingleAmmo = ownUnit.Ammunition;
        _cachedSingleReloadTicks = ownUnit.ReloadTicksRemaining;
        _cachedSingleAttackStatus = ownUnit.AttackStatus;
        _cachedSingleTargetContact = ownUnit.TargetContactId;
        _cachedSingleStance = ownUnit.Stance;
        _cachedSingleNavStatus = ownUnit.NavStatus;
        _cachedSinglePassengers = ownUnit.PassengerCount;
        _cachedSingleCapacity = ownUnit.PassengerCapacity;
        _cachedSingleControlGroup = controlGroup;
        VisibleRosterChipCount = 0;
        _cachedSingleHasFlight = ownUnit.HasFlightCapability;
        _cachedSingleFlightPhase = ownUnit.FlightPhase;
        _cachedSingleAltitudeBand = ownUnit.AltitudeBand;
        _cachedSingleAltMeters = altMeters;
        _cachedSingleSlingLoadId = ownUnit.SlingLoadId;
        _cachedSingleSlungById = ownUnit.SlungById;
        _cachedSingleFlightFlags = ownUnit.FlightFlags;
        _cachedSingleFlareCharges = ownUnit.FlareCharges;
        _cachedSingleFlareCooldownSec = flareCooldownSec;
        _cachedSingleCmFlags = ownUnit.CountermeasureFlags;

        UnitDefinition? def = ownUnit.Definition < _catalog.Units.Length ? _catalog.Units[ownUnit.Definition] : null;
        string name = def is not null ? FormatUnitName(def) : $"Unit #{ownUnit.Id.Value}";
        string role = def is not null ? GetRoleName(def) : "Combat Unit";
        ContactClassification classification = def is not null ? ParseClassification(def.Classification) : ContactClassification.Vehicle;
        _headerSymbol.Update(classification, false, false, selected: true);

        _titleLabel.Text = controlGroup > 0 ? $"{name} / Group {controlGroup}" : name;
        _titleLabel.TooltipText = $"{role} / Unit #{ownUnit.Id.Value}";
        SetLabelColor(_titleLabel, HudTheme.Text);
        _roleTagLabel.Text = GetShortRoleTag(def);
        SetLabelColor(_roleTagLabel, HudTheme.Muted);

        int maxHealth = def?.MaxHealth ?? 100;
        int hpPct = maxHealth > 0 ? (int)(ownUnit.Health * 100f / maxHealth) : 0;
        if (ownUnit.HasFlightCapability)
        {
            string phaseStr = ownUnit.FlightPhase switch
            {
                FlightPhase.Ground => "Landed",
                FlightPhase.TakingOff => "Taking Off",
                FlightPhase.Airborne => "Airborne",
                FlightPhase.Landing => "Landing",
                FlightPhase.Crashing => "CRASHING",
                _ => ownUnit.FlightPhase.ToString()
            };
            string bandStr = ownUnit.AltitudeBand == AltitudeBand.Low ? "NOE Low" : "Cruise High";
            string cargoExtra = (ownUnit.FlightFlags & FlightStateFlags.FastRopeActive) != 0
                ? " | FAST-ROPING"
                : ownUnit.SlingLoadId != default
                    ? $" | Sling #{ownUnit.SlingLoadId.Value}"
                    : string.Empty;
            bool hasFlares = (ownUnit.CountermeasureFlags & CountermeasureFlags.HasFlares) != 0;
            bool missileWarning = (ownUnit.CountermeasureFlags & CountermeasureFlags.MissileWarning) != 0;
            string flareExtra = string.Empty;
            if (hasFlares)
            {
                int maxFlares = def?.Flares?.Charges ?? ownUnit.FlareCharges;
                string autoTag = (ownUnit.CountermeasureFlags & CountermeasureFlags.AutoFlares) != 0 ? "AUTO" : "MAN";
                flareExtra = missileWarning
                    ? $" | MISSILE WARNING! FLARES {ownUnit.FlareCharges}/{maxFlares}"
                    : $" | Flares {ownUnit.FlareCharges}/{maxFlares} ({autoTag})";
            }
            _line1Label.Text = $"{ownUnit.Health} / {maxHealth} HP ({hpPct}%) | {phaseStr} ({altMeters}m, {bandStr}){cargoExtra}{flareExtra}";
            SetLabelColor(_line1Label, (hpPct <= 25 || missileWarning) ? HudTheme.Danger : HudTheme.Text);
        }
        else if (ownUnit.SlungById != default)
        {
            _line1Label.Text = $"{ownUnit.Health} / {maxHealth} HP ({hpPct}%) | External Sling Cargo (Carrier #{ownUnit.SlungById.Value})";
            SetLabelColor(_line1Label, hpPct <= 25 ? HudTheme.Danger : HudTheme.Text);
        }
        else
        {
            _line1Label.Text = $"{ownUnit.Health} / {maxHealth} HP ({hpPct}%)";
            SetLabelColor(_line1Label, hpPct <= 25 ? HudTheme.Danger : HudTheme.Text);
        }

        WeaponData? ownWeapon = def?.Weapon is null ? null : FindWeapon(def.Weapon);
        int reloadDuration = ownWeapon is null ? 0 : ownWeapon.ReloadDurationTicks > 0
            ? ownWeapon.ReloadDurationTicks : (int)Math.Round(ownWeapon.ReloadSeconds / Simulation.TickSeconds);
        WeaponReadinessPresentation readiness = WeaponReadinessPresentation.From(
            def?.Weapon is not null, ownUnit.Ammunition, ownUnit.ReloadTicksRemaining, reloadDuration);
        _bars.Visible = true;
        _bars.Update(ownUnit.Health / (float)Math.Max(1, maxHealth), readiness);
        _readinessLabel.Visible = readiness.State != WeaponReadinessState.Unarmed;
        _readinessLabel.Text = readiness.Label;
        SetLabelColor(_readinessLabel, readiness.State == WeaponReadinessState.Empty ? HudTheme.Danger : HudTheme.Accent);

        // Weaponry & readiness
        if (def?.Weapon is null)
        {
            _line2Label.Text = "Armament: Unarmed";
            _line2Label.TooltipText = string.Empty;
            SetLabelColor(_line2Label, HudTheme.Muted);
        }
        else
        {
            string weaponName = ownWeapon is not null ? FormatWeaponName(ownWeapon.Id) : def.Weapon;
            int maxAmmo = ownWeapon?.AmmoCapacity ?? 10;
            float reloadSec = ownUnit.ReloadTicksRemaining / (float)Simulation.TickRate;
            if (readiness.State == WeaponReadinessState.Reloading)
            {
                _readinessLabel.Text = $"RELOADING {reloadSec.ToString("0.0", CultureInfo.InvariantCulture)}s";
            }
            _line2Label.Text = $"{weaponName}: {ownUnit.Ammunition} / {maxAmmo}";
            _line2Label.TooltipText = ownWeapon is not null && ownWeapon.Range > 0f
                ? $"{weaponName} (Range {ownWeapon.Range:F0}m)"
                : weaponName;
            SetLabelColor(_line2Label, ownUnit.Ammunition == 0 ? HudTheme.Danger : HudTheme.Text);
        }

        // Server-reported attack and navigation status
        string attackStr = ownUnit.AttackStatus switch
        {
            AttackExecutionStatus.None => string.Empty,
            AttackExecutionStatus.Aiming => "Aiming",
            AttackExecutionStatus.OutOfRange => "Out of range",
            AttackExecutionStatus.Obstructed => "Line of fire blocked",
            AttackExecutionStatus.NoAmmo => "Out of Ammo",
            AttackExecutionStatus.TargetLost => "Target lost",
            _ => ownUnit.AttackStatus.ToString()
        };

        // Keep critical combat errors visible ahead of clearance waiting so text and color agree.
        bool criticalAttack = ownUnit.AttackStatus is AttackExecutionStatus.NoAmmo or AttackExecutionStatus.TargetLost;
        string orderStatus = !criticalAttack && ownUnit.NavStatus == PathRequestStatus.WaitingForClearance
            ? "Waiting for clearance"
            : attackStr;
        _line3Label.Text = orderStatus.Length == 0 ? FormatStance(ownUnit.Stance) : $"{FormatStance(ownUnit.Stance)} / {orderStatus}";
        _line3Label.TooltipText = ownUnit.TargetContactId.Value > 0 ? $"Attack target: Contact #{ownUnit.TargetContactId.Value}" : "No attack order";
        SetLabelColor(_line3Label, criticalAttack
            ? HudTheme.Danger
            : ownUnit.NavStatus == PathRequestStatus.WaitingForClearance
                ? HudTheme.Accent
                : ownUnit.AttackStatus switch
                {
                    AttackExecutionStatus.Aiming => HudTheme.Accent,
                    AttackExecutionStatus.OutOfRange or AttackExecutionStatus.Obstructed => HudTheme.Accent,
                    _ => ownUnit.Stance == FireStance.HoldFire ? HudTheme.Accent : HudTheme.Muted
                });

        string specs = FormatTacticalSpecs(def, ownWeapon, ownUnit.PassengerCount, ownUnit.PassengerCapacity);
        _specsLabel.Text = specs;
        _specsLabel.Visible = specs.Length > 0;
        SetLabelColor(_specsLabel, HudTheme.Muted);
    }

    public void UpdateMultipleOwnUnits(
        int totalCount,
        int tankCount,
        int reconCount,
        int otherCount,
        int controlGroup = 0)
    {
        UpdateMultipleOwnUnitsDetailed(
            totalCount,
            tankCount,
            reconCount,
            apcCount: 0,
            infantryCount: 0,
            commandCount: 0,
            otherCount,
            damagedCount: 0,
            emptyAmmoCount: 0,
            controlGroup,
            selected: null,
            replica: null);
    }

    public void UpdateMultipleOwnUnits(
        HashSet<EntityId> selected,
        ClientReplica replica,
        int controlGroup = 0)
    {
        int total = selected.Count;
        int tanks = 0, recons = 0, apcs = 0, infantry = 0, commands = 0, others = 0;
        int damaged = 0, emptyAmmo = 0;
        uint stateHash = 17;

        foreach (EntityId id in selected)
        {
            stateHash = unchecked(stateHash * 31 + id.Value);
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation u))
            {
                others++;
                continue;
            }
            stateHash = unchecked(stateHash * 31 + u.Health);
            stateHash = unchecked(stateHash * 31 + (u.Ammunition == 0 ? 1u : 2u));

            UnitDefinition? def = u.Definition < _catalog.Units.Length ? _catalog.Units[u.Definition] : null;
            int maxHp = def?.MaxHealth ?? 100;
            if (u.Health < maxHp) damaged++;
            if (def?.Weapon is not null && u.Ammunition == 0) emptyAmmo++;

            string cls = def?.Classification ?? string.Empty;
            if (cls.Equals("tank", StringComparison.OrdinalIgnoreCase)) tanks++;
            else if (cls.Equals("recon", StringComparison.OrdinalIgnoreCase)) recons++;
            else if (cls.Equals("apc", StringComparison.OrdinalIgnoreCase)) apcs++;
            else if (cls.Equals("infantry", StringComparison.OrdinalIgnoreCase)) infantry++;
            else if (cls.Equals("command_vehicle", StringComparison.OrdinalIgnoreCase) || cls.Equals("command", StringComparison.OrdinalIgnoreCase)) commands++;
            else others++;
        }

        UpdateMultipleOwnUnitsDetailed(
            total,
            tanks,
            recons,
            apcs,
            infantry,
            commands,
            others,
            damaged,
            emptyAmmo,
            controlGroup,
            selected,
            replica,
            stateHash);
    }

    private void UpdateMultipleOwnUnitsDetailed(
        int totalCount,
        int tankCount,
        int reconCount,
        int apcCount,
        int infantryCount,
        int commandCount,
        int otherCount,
        int damagedCount,
        int emptyAmmoCount,
        int controlGroup,
        HashSet<EntityId>? selected,
        ClientReplica? replica,
        uint rosterStateHash = 0)
    {
        if (_mode == DisplayMode.MultipleUnits &&
            _cachedMultiTotal == totalCount &&
            _cachedMultiTanks == tankCount &&
            _cachedMultiRecons == reconCount &&
            _cachedMultiApcs == apcCount &&
            _cachedMultiInfantry == infantryCount &&
            _cachedMultiCommands == commandCount &&
            _cachedMultiOthers == otherCount &&
            _cachedMultiDamaged == damagedCount &&
            _cachedMultiEmpty == emptyAmmoCount &&
            _cachedMultiGroup == controlGroup &&
            _cachedRosterStateHash == rosterStateHash)
        {
            return;
        }

        SetDisplayMode(DisplayMode.MultipleUnits);
        _bars.Visible = false;
        _readinessLabel.Visible = false;
        _cachedMultiTotal = totalCount;
        _cachedMultiTanks = tankCount;
        _cachedMultiRecons = reconCount;
        _cachedMultiApcs = apcCount;
        _cachedMultiInfantry = infantryCount;
        _cachedMultiCommands = commandCount;
        _cachedMultiOthers = otherCount;
        _cachedMultiDamaged = damagedCount;
        _cachedMultiEmpty = emptyAmmoCount;
        _cachedMultiGroup = controlGroup;
        _cachedRosterStateHash = rosterStateHash;

        _titleLabel.Text = controlGroup > 0
            ? $"{totalCount} Units Selected / Group {controlGroup}"
            : $"{totalCount} Units Selected";
        _titleLabel.TooltipText = "Orders apply to eligible selected units.";
        SetLabelColor(_titleLabel, HudTheme.Text);
        _roleTagLabel.Text = controlGroup > 0 ? $"GRP {controlGroup}" : "GROUP";
        SetLabelColor(_roleTagLabel, HudTheme.Accent);

        _line1Label.Text = BuildCompositionText(tankCount, reconCount, apcCount, infantryCount, commandCount, otherCount);
        SetLabelColor(_line1Label, HudTheme.Muted);
        _line2Label.Text = string.Empty;
        _line3Label.Text = string.Empty;

        UpdateRosterChips(selected, replica, totalCount);

        if (damagedCount > 0 && emptyAmmoCount > 0)
        {
            _specsLabel.Text = $"{damagedCount} damaged  |  {emptyAmmoCount} NO AMMO";
            SetLabelColor(_specsLabel, HudTheme.Danger);
            _specsLabel.Visible = true;
        }
        else if (emptyAmmoCount > 0)
        {
            _specsLabel.Text = $"{emptyAmmoCount} unit(s) out of ammo";
            SetLabelColor(_specsLabel, HudTheme.Danger);
            _specsLabel.Visible = true;
        }
        else if (damagedCount > 0)
        {
            _specsLabel.Text = $"{damagedCount} unit(s) damaged";
            SetLabelColor(_specsLabel, HudTheme.Accent);
            _specsLabel.Visible = true;
        }
        else if (VisibleRosterChipCount > 0)
        {
            _specsLabel.Text = "Click chip to focus unit  |  Shift-click to remove";
            SetLabelColor(_specsLabel, HudTheme.Muted);
            _specsLabel.Visible = true;
        }
        else
        {
            _specsLabel.Text = string.Empty;
            _specsLabel.Visible = false;
        }
    }

    private void UpdateRosterChips(HashSet<EntityId>? selected, ClientReplica? replica, int totalCount)
    {
        if (selected is null || replica is null)
        {
            VisibleRosterChipCount = 0;
            _rosterGrid.Visible = false;
            for (int i = 0; i < MaxRosterChips; i++) _rosterChips[i].Visible = false;
            return;
        }

        int slot = 0;
        foreach (EntityId id in selected)
        {
            if (slot >= MaxRosterChips) break;
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation u)) continue;
            UnitDefinition? def = u.Definition < _catalog.Units.Length ? _catalog.Units[u.Definition] : null;
            string shortName = FormatShortUnitName(def, u.Id);
            int maxHp = def?.MaxHealth ?? 100;
            int hpPct = maxHp > 0 ? (int)(u.Health * 100f / maxHp) : 0;
            bool outOfAmmo = def?.Weapon is not null && u.Ammunition == 0;

            Button chip = _rosterChips[slot];
            _rosterChipIds[slot] = id;
            chip.Text = outOfAmmo ? $"{shortName} [0]" : $"{shortName} {hpPct}%";
            chip.TooltipText = outOfAmmo
                ? $"{shortName}: {hpPct}% HP, NO AMMO. Click to focus, Shift-click to remove."
                : $"{shortName}: {hpPct}% HP. Click to focus, Shift-click to remove.";
            chip.AddThemeColorOverride("font_color", outOfAmmo || hpPct <= 25 ? HudTheme.Danger : (hpPct < 100 ? HudTheme.Accent : HudTheme.Text));
            chip.Visible = true;
            slot++;
        }

        if (totalCount > MaxRosterChips && slot == MaxRosterChips)
        {
            int overflow = totalCount - (MaxRosterChips - 1);
            _rosterChips[MaxRosterChips - 1].Text = $"+{overflow} more";
            _rosterChips[MaxRosterChips - 1].TooltipText = $"{overflow} additional units selected.";
            _rosterChips[MaxRosterChips - 1].AddThemeColorOverride("font_color", HudTheme.Muted);
            _rosterChipIds[MaxRosterChips - 1] = default;
        }

        for (int i = slot; i < MaxRosterChips; i++)
        {
            _rosterChips[i].Visible = false;
            _rosterChipIds[i] = default;
        }

        VisibleRosterChipCount = slot;
        _rosterGrid.Visible = slot > 0;
    }

    private void OnRosterChipPressed(int slot)
    {
        if (slot < 0 || slot >= MaxRosterChips) return;
        EntityId id = _rosterChipIds[slot];
        if (id.Value == 0) return;
        bool shift = Input.IsKeyPressed(Key.Shift);
        RosterUnitClicked?.Invoke(id, shift);
    }

    private string BuildCompositionText(int tanks, int recons, int apcs, int infantry, int commands, int others)
    {
        _compositionBuilder.Clear();
        AppendCount(tanks, "Tank", "Tanks");
        AppendCount(recons, "Recon", "Recon");
        AppendCount(apcs, "APC", "APCs");
        AppendCount(infantry, "Infantry", "Infantry");
        AppendCount(commands, "Command", "Command");
        AppendCount(others, "Other", "Other");
        return _compositionBuilder.ToString();
    }

    private void AppendCount(int count, string singular, string plural)
    {
        if (count <= 0) return;
        if (_compositionBuilder.Length > 0) _compositionBuilder.Append(", ");
        _compositionBuilder.Append(count);
        _compositionBuilder.Append(' ');
        _compositionBuilder.Append(count == 1 ? singular : plural);
    }

    public void UpdateContact(ContactObservation contact, int serverTick)
    {
        int ageTicks = Math.Max(0, serverTick - contact.LastSeenTick);
        int ageKey = ageTicks / 2;
        if (_mode == DisplayMode.Contact &&
            _cachedContactId == contact.Id &&
            _cachedContactState == contact.State &&
            _cachedContactType == contact.Type &&
            _cachedContactClassification == contact.Classification &&
            _cachedContactAgeKey == ageKey)
        {
            return;
        }

        SetDisplayMode(DisplayMode.Contact);
        _bars.Visible = false;
        _readinessLabel.Visible = false;
        VisibleRosterChipCount = 0;
        _cachedContactId = contact.Id;
        _cachedContactState = contact.State;
        _cachedContactType = contact.Type;
        _cachedContactClassification = contact.Classification;
        _cachedContactAgeKey = ageKey;

        _headerSymbol.Update(contact.Classification, true, contact.State == ContactState.LastKnown, false);
        _titleLabel.Text = $"Enemy Contact #{contact.Id.Value}";
        SetLabelColor(_titleLabel, HudTheme.Danger);
        _roleTagLabel.Text = contact.State == ContactState.Current ? "LIVE TRACK" : "LAST KNOWN";
        SetLabelColor(_roleTagLabel, contact.State == ContactState.Current ? HudTheme.Danger : HudTheme.Muted);

        _line1Label.Text = contact.State == ContactState.Current ? "Currently observed" : "Last-known position";
        SetLabelColor(_line1Label, HudTheme.Text);

        UnitDefinition? identifiedDef = contact.Type < _catalog.Units.Length ? _catalog.Units[contact.Type] : null;
        string typeName;
        if (identifiedDef is not null)
        {
            typeName = identifiedDef.Name is not null
                ? $"{identifiedDef.Name} [{FormatClassification(contact.Classification)}]"
                : GetRoleName(identifiedDef);
        }
        else
        {
            typeName = FormatClassification(contact.Classification);
        }

        _line2Label.Text = typeName;
        SetLabelColor(_line2Label, HudTheme.Danger);

        float ageSeconds = ageTicks / (float)Simulation.TickRate;
        string ageFormatted = ageSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string tacticalHint = contact.State == ContactState.Current
            ? $"Seen {ageFormatted}s ago"
            : $"Lost {ageFormatted}s ago / target position unconfirmed";

        _line3Label.Text = tacticalHint;
        SetLabelColor(_line3Label, contact.State == ContactState.Current ? HudTheme.Accent : HudTheme.Muted);

        // Public catalog reference specs for identified enemy types (never private HP or ammo).
        WeaponData? refWeapon = identifiedDef?.Weapon is null ? null : FindWeapon(identifiedDef.Weapon);
        string refSpecs = FormatContactReferenceSpecs(identifiedDef, refWeapon);
        _specsLabel.Text = refSpecs;
        _specsLabel.Visible = refSpecs.Length > 0;
        SetLabelColor(_specsLabel, HudTheme.Muted);
    }

    private static string FormatTacticalSpecs(UnitDefinition? def, WeaponData? weapon, byte passengers, byte capacity)
    {
        if (def is null) return string.Empty;
        string paxPart = capacity > 0 ? $"PAX {passengers}/{capacity}  |  " : string.Empty;
        string armorPart = def.ArmorFront > 0f ? $"ARM {def.ArmorFront:F0}/{def.ArmorSide:F0}  |  " : string.Empty;
        string rangePart = weapon is not null && weapon.Range > 0f ? $"RNG {weapon.Range:F0}m  |  " : string.Empty;
        string sensorPart = def.SensorRange > 0f ? $"SNS {def.SensorRange:F0}m" : string.Empty;
        string result = $"{paxPart}{armorPart}{rangePart}{sensorPart}";
        return result.EndsWith("  |  ", StringComparison.Ordinal) ? result[..^5] : result;
    }

    private static string FormatContactReferenceSpecs(UnitDefinition? def, WeaponData? weapon)
    {
        if (def is null) return string.Empty;
        string weaponPart = weapon is not null
            ? $"{FormatWeaponName(weapon.Id)} ({weapon.Range:F0}m)"
            : (def.Weapon is null ? "Unarmed" : def.Weapon);
        string armorPart = def.ArmorFront > 0f ? $"  |  ARM {def.ArmorFront:F0}/{def.ArmorSide:F0}" : string.Empty;
        return $"REF: {weaponPart}{armorPart}";
    }

    private static string FormatShortUnitName(UnitDefinition? def, EntityId id)
    {
        if (def is null) return $"#{id.Value}";
        if (string.Equals(def.Classification, "infantry", StringComparison.OrdinalIgnoreCase))
            return "Rifle";
        if (!string.IsNullOrEmpty(def.Name))
        {
            return def.Name.Length > 7 ? def.Name[..7] : def.Name;
        }
        return def.Classification?.ToLowerInvariant() switch
        {
            "tank" => "Tank",
            "recon" => "Recon",
            "apc" => "APC",
            "command_vehicle" or "command" => "CMD",
            _ => $"#{id.Value}"
        };
    }

    private static string FormatStance(FireStance stance) => stance switch
    {
        FireStance.FireAtWill => "Fire at will",
        FireStance.ReturnFire => "Return fire",
        FireStance.HoldFire => "Hold fire",
        _ => "Fire at will"
    };

    private static ContactClassification ParseClassification(string? classification) => classification?.ToLowerInvariant() switch
    {
        "tank" => ContactClassification.Tank,
        "recon" => ContactClassification.Recon,
        "apc" => ContactClassification.Apc,
        "infantry" => ContactClassification.Infantry,
        "command_vehicle" or "command" => ContactClassification.CommandVehicle,
        "helicopter" => ContactClassification.Helicopter,
        "artillery" or "mlrs" => ContactClassification.Artillery,
        "anti_air" or "antiair" or "sam" or "aa" => ContactClassification.AntiAir,
        _ => ContactClassification.Vehicle
    };

    private WeaponData? FindWeapon(string weaponId)
    {
        if (_catalog.Weapons is null) return null;
        for (int i = 0; i < _catalog.Weapons.Length; i++)
        {
            if (string.Equals(_catalog.Weapons[i].Id, weaponId, StringComparison.Ordinal))
                return _catalog.Weapons[i];
        }
        return null;
    }

    private static string GetShortRoleTag(UnitDefinition? def) => def?.Classification?.ToLowerInvariant() switch
    {
        "tank" => "TANK",
        "recon" => "RECON",
        "apc" => "APC",
        "command_vehicle" or "command" => "CMD",
        "infantry" => "INFANTRY",
        "helicopter" => "HELI",
        "artillery" or "mlrs" => "ARTY",
        "anti_air" or "antiair" or "sam" or "aa" => "AA",
        _ => "UNIT"
    };

    private static string GetRoleName(UnitDefinition def)
    {
        if (!string.IsNullOrEmpty(def.Classification))
        {
            return def.Classification.ToLowerInvariant() switch
            {
                "tank" => "Combat Tank",
                "recon" => "Scout",
                "apc" => "Armored Personnel Carrier",
                "command_vehicle" => "Command Vehicle",
                "infantry" => "Infantry",
                "helicopter" => "Transport Helicopter",
                "artillery" or "mlrs" => "Rocket Artillery",
                "anti_air" or "antiair" or "sam" or "aa" => "Surface-to-Air Missile",
                _ => def.Classification
            };
        }
        if (def.Id.Contains("tank", StringComparison.OrdinalIgnoreCase)) return "Combat Tank";
        if (def.Id.Contains("recon", StringComparison.OrdinalIgnoreCase)) return "Scout";
        if (def.Id.Contains("command", StringComparison.OrdinalIgnoreCase)) return "Command Vehicle";
        return "Vehicle";
    }

    // Historical types carry their real name; prototype units fall back to a readable ID.
    internal static string FormatUnitName(UnitDefinition def) => def.Name ?? def.Id switch
    {
        "prototype_tank" => "Prototype Tank",
        "recon_vehicle" => "Reconnaissance Vehicle",
        "apc" => "Armored Personnel Carrier",
        "prototype_vehicle" => "Prototype Vehicle",
        _ => def.Id
    };

    private static string FormatWeaponName(string id) => id switch
    {
        "tank_gun_100mm" => "100mm Tank Gun",
        "gun_115mm_u5ts" => "115mm U-5TS Gun",
        "gun_105mm_m68" => "105mm M68 Gun",
        "mg_14_5mm_kpvt" => "14.5mm KPVT HMG",
        "mg_12_7mm_m2hb" => "12.7mm M2HB HMG",
        "mg_7_62mm_m60" => "7.62mm M60 MG",
        "mg_7_62mm_m60d" => "Twin 7.62mm M60D Door Guns",
        "rifle_5_56mm_m16a1" => "5.56mm M16A1 Rifle",
        "rocket_122mm_9m22u" => "122mm 9M22U Rockets",
        "sam_9m37" => "9M37M IR SAM",
        _ => id
    };

    private static string FormatClassification(ContactClassification classification) => classification switch
    {
        ContactClassification.Tank => "Tank",
        ContactClassification.Recon => "Recon Vehicle",
        ContactClassification.Apc => "APC",
        ContactClassification.CommandVehicle => "Command Vehicle",
        ContactClassification.Infantry => "Infantry",
        ContactClassification.Helicopter => "Helicopter",
        ContactClassification.Artillery => "Artillery",
        ContactClassification.AntiAir => "Air Defense SAM",
        ContactClassification.Vehicle => "Vehicle",
        _ => "Unknown"
    };
}
