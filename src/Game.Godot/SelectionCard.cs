using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

// Displays persistent tactical selection details for own units and enemy contacts.
internal sealed partial class SelectionCard : PanelContainer
{
    private readonly Label _titleLabel;
    private readonly Label _line1Label;
    private readonly Label _line2Label;
    private readonly Label _line3Label;
    private readonly UnitCatalog _catalog;

    public string TitleText => _titleLabel.Text;
    public string Line1Text => _line1Label.Text;
    public string Line2Text => _line2Label.Text;
    public string Line3Text => _line3Label.Text;

    public SelectionCard(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(360, 96);

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.1f, 0.12f, 0.15f, 0.90f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.35f, 0.45f, 0.55f, 0.75f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        AddThemeStyleboxOverride("panel", style);

        VBoxContainer vbox = new() { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(vbox);

        _titleLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _titleLabel.AddThemeFontSizeOverride("font_size", 13);
        _titleLabel.Modulate = new Color("edc76b");
        vbox.AddChild(_titleLabel);

        _line1Label = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _line1Label.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_line1Label);

        _line2Label = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _line2Label.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_line2Label);

        _line3Label = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _line3Label.AddThemeFontSizeOverride("font_size", 11);
        _line3Label.Modulate = new Color("9bc4e2");
        vbox.AddChild(_line3Label);

        UpdateEmpty();
    }

    private static readonly string[] SingleUnitGroupTexts =
    [
        " (1 Unit)",
        " (1 Unit, Group 1)",
        " (1 Unit, Group 2)",
        " (1 Unit, Group 3)",
        " (1 Unit, Group 4)",
        " (1 Unit, Group 5)",
        " (1 Unit, Group 6)",
        " (1 Unit, Group 7)",
        " (1 Unit, Group 8)",
        " (1 Unit, Group 9)"
    ];

    private enum DisplayMode { None, Empty, SingleUnit, MultipleUnits, Contact }
    private DisplayMode _mode = DisplayMode.None;

    // Single unit cached state
    private EntityId _cachedSingleId;
    private ushort _cachedSingleDef;
    private ushort _cachedSingleHealth;
    private ushort _cachedSingleAmmo;
    private ushort _cachedSingleReloadTicks;
    private AttackExecutionStatus _cachedSingleAttackStatus;
    private ContactId _cachedSingleTargetContact;
    private int _cachedSingleControlGroup = -1;

    // Multiple units cached state
    private int _cachedMultiTotal = -1;
    private int _cachedMultiTanks = -1;
    private int _cachedMultiRecons = -1;
    private int _cachedMultiOthers = -1;
    private int _cachedMultiGroup = -1;

    // Contact cached state
    private ContactId _cachedContactId;
    private ContactState _cachedContactState;
    private DetectionLevel _cachedContactLevel;
    private ContactClassification _cachedContactClassification;
    private int _cachedContactAgeKey = -1;

    public void UpdateEmpty()
    {
        if (_mode == DisplayMode.Empty) return;
        _mode = DisplayMode.Empty;

        _titleLabel.Text = "No Selection";
        _titleLabel.Modulate = new Color("a0a8b0");
        _line1Label.Text = "Left-click a friendly unit or enemy contact to inspect.";
        _line2Label.Text = string.Empty;
        _line3Label.Text = string.Empty;
    }

    public void UpdateSingleOwnUnit(OwnUnitObservation ownUnit, int controlGroup = 0)
    {
        if (_mode == DisplayMode.SingleUnit &&
            _cachedSingleId == ownUnit.Id &&
            _cachedSingleDef == ownUnit.Definition &&
            _cachedSingleHealth == ownUnit.Health &&
            _cachedSingleAmmo == ownUnit.Ammunition &&
            _cachedSingleReloadTicks == ownUnit.ReloadTicksRemaining &&
            _cachedSingleAttackStatus == ownUnit.AttackStatus &&
            _cachedSingleTargetContact == ownUnit.TargetContactId &&
            _cachedSingleControlGroup == controlGroup)
        {
            return;
        }

        _mode = DisplayMode.SingleUnit;
        _cachedSingleId = ownUnit.Id;
        _cachedSingleDef = ownUnit.Definition;
        _cachedSingleHealth = ownUnit.Health;
        _cachedSingleAmmo = ownUnit.Ammunition;
        _cachedSingleReloadTicks = ownUnit.ReloadTicksRemaining;
        _cachedSingleAttackStatus = ownUnit.AttackStatus;
        _cachedSingleTargetContact = ownUnit.TargetContactId;
        _cachedSingleControlGroup = controlGroup;

        UnitDefinition? def = ownUnit.Definition < _catalog.Units.Length ? _catalog.Units[ownUnit.Definition] : null;
        string name = def is not null ? FormatUnitName(def) : $"Unit #{ownUnit.Id.Value}";
        string role = def is not null ? GetRoleName(def) : "Combat Unit";

        string groupText = (controlGroup >= 0 && controlGroup < SingleUnitGroupTexts.Length)
            ? SingleUnitGroupTexts[controlGroup]
            : (controlGroup > 0 ? $" (1 Unit, Group {controlGroup})" : " (1 Unit)");
        _titleLabel.Text = $"Friendly Unit #{ownUnit.Id.Value} - {name} [{role}]{groupText}";
        _titleLabel.Modulate = new Color("5cd65c");

        int maxHealth = def?.MaxHealth ?? 100;
        int hpPct = maxHealth > 0 ? (int)(ownUnit.Health * 100f / maxHealth) : 0;
        _line1Label.Text = $"Health: {ownUnit.Health} / {maxHealth} HP ({hpPct}%)";

        // Weaponry & readiness
        if (def?.Weapon is null)
        {
            _line2Label.Text = "Armament: Unarmed";
            _line2Label.Modulate = new Color("c0c0c0");
        }
        else
        {
            WeaponData? weapon = FindWeapon(def.Weapon);
            string weaponName = weapon is not null ? FormatWeaponName(weapon.Id) : def.Weapon;
            int maxAmmo = weapon?.AmmoCapacity ?? 10;
            string ammoStr = ownUnit.Ammunition > 0 ? $"{ownUnit.Ammunition} / {maxAmmo}" : $"0 / {maxAmmo} (Depleted)";
            float reloadSec = ownUnit.ReloadTicksRemaining / (float)Simulation.TickRate;
            string reloadStr = ownUnit.ReloadTicksRemaining == 0
                ? "READY"
                : $"{reloadSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s";

            _line2Label.Text = $"Armament: {weaponName} | Ammo: {ammoStr} | Gun: {reloadStr}";
            _line2Label.Modulate = ownUnit.Ammunition == 0 ? new Color("e6a15c") : Colors.White;
        }

        // Server-reported attack order status
        string attackStr = ownUnit.AttackStatus switch
        {
            AttackExecutionStatus.None => "No attack order",
            AttackExecutionStatus.Aiming => $"Aiming -> Contact #{ownUnit.TargetContactId.Value}",
            AttackExecutionStatus.OutOfRange => $"Out of Range -> Contact #{ownUnit.TargetContactId.Value} (Holding Fire)",
            AttackExecutionStatus.Obstructed => $"Obstructed -> Contact #{ownUnit.TargetContactId.Value} (Holding Fire)",
            AttackExecutionStatus.NoAmmo => "Out of Ammo (Holding Fire)",
            AttackExecutionStatus.TargetLost => "Target Lost (Holding Fire)",
            _ => ownUnit.AttackStatus.ToString()
        };

        _line3Label.Text = $"Attack Order: {attackStr}";
        _line3Label.Modulate = ownUnit.AttackStatus switch
        {
            AttackExecutionStatus.Aiming => new Color("edc76b"),
            AttackExecutionStatus.OutOfRange or AttackExecutionStatus.Obstructed => new Color("e09267"),
            AttackExecutionStatus.NoAmmo or AttackExecutionStatus.TargetLost => new Color("e06767"),
            _ => new Color("9bc4e2")
        };
    }

    public void UpdateMultipleOwnUnits(int totalCount, int tankCount, int reconCount, int otherCount, int controlGroup = 0)
    {
        if (_mode == DisplayMode.MultipleUnits &&
            _cachedMultiTotal == totalCount &&
            _cachedMultiTanks == tankCount &&
            _cachedMultiRecons == reconCount &&
            _cachedMultiOthers == otherCount &&
            _cachedMultiGroup == controlGroup)
        {
            return;
        }

        _mode = DisplayMode.MultipleUnits;
        _cachedMultiTotal = totalCount;
        _cachedMultiTanks = tankCount;
        _cachedMultiRecons = reconCount;
        _cachedMultiOthers = otherCount;
        _cachedMultiGroup = controlGroup;

        _titleLabel.Text = controlGroup > 0
            ? $"Friendly Group (Group {controlGroup}) - {totalCount} Units Selected"
            : $"Friendly Group - {totalCount} Units Selected";
        _titleLabel.Modulate = new Color("5cd65c");

        string tankPart = tankCount > 0 ? $"{tankCount} {(tankCount == 1 ? "Tank" : "Tanks")}" : string.Empty;
        string reconPart = reconCount > 0 ? $"{reconCount} Recon" : string.Empty;
        string otherPart = otherCount > 0 ? $"{otherCount} Other" : string.Empty;

        string composition;
        if (tankPart.Length > 0 && reconPart.Length > 0 && otherPart.Length > 0)
            composition = $"{tankPart}, {reconPart}, {otherPart}";
        else if (tankPart.Length > 0 && reconPart.Length > 0)
            composition = $"{tankPart}, {reconPart}";
        else if (tankPart.Length > 0 && otherPart.Length > 0)
            composition = $"{tankPart}, {otherPart}";
        else if (reconPart.Length > 0 && otherPart.Length > 0)
            composition = $"{reconPart}, {otherPart}";
        else
            composition = tankPart.Length > 0 ? tankPart : (reconPart.Length > 0 ? reconPart : otherPart);

        _line1Label.Text = $"Composition: {composition}";
        _line2Label.Text = "Group selection active. Individual weapon readiness not aggregated.";
        _line2Label.Modulate = new Color("a0a8b0");
        _line3Label.Text = "Right-click terrain to move group, right-click enemy contact to attack.";
        _line3Label.Modulate = new Color("9bc4e2");
    }

    public void UpdateContact(ContactObservation contact, int serverTick)
    {
        int ageTicks = Math.Max(0, serverTick - contact.LastSeenTick);
        int ageKey = ageTicks / 2;
        if (_mode == DisplayMode.Contact &&
            _cachedContactId == contact.Id &&
            _cachedContactState == contact.State &&
            _cachedContactLevel == contact.Level &&
            _cachedContactClassification == contact.Classification &&
            _cachedContactAgeKey == ageKey)
        {
            return;
        }

        _mode = DisplayMode.Contact;
        _cachedContactId = contact.Id;
        _cachedContactState = contact.State;
        _cachedContactLevel = contact.Level;
        _cachedContactClassification = contact.Classification;
        _cachedContactAgeKey = ageKey;

        _titleLabel.Text = $"Enemy Contact #{contact.Id.Value}";
        _titleLabel.Modulate = new Color("ff4020");

        string stateStr = contact.State == ContactState.Current ? "Current Observation (Live Track)" : "Last Known Position (Frozen Marker)";
        _line1Label.Text = $"State: {stateStr}";

        string classStr = contact.Level == DetectionLevel.Identified
            ? $"Identified {FormatClassification(contact.Classification)}"
            : "Detected (Unknown Type)";
        _line2Label.Text = $"Classification: {classStr}";
        _line2Label.Modulate = contact.Level == DetectionLevel.Identified ? new Color("ff8060") : new Color("e0a080");

        float ageSeconds = ageTicks / (float)Simulation.TickRate;
        string ageFormatted = ageSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string tacticalHint = contact.State == ContactState.Current
            ? $"Seen {ageFormatted}s ago / right-click with selected tank to attack"
            : $"Lost {ageFormatted}s ago / target position unconfirmed";

        _line3Label.Text = $"Age: {tacticalHint}";
        _line3Label.Modulate = contact.State == ContactState.Current ? new Color("edc76b") : new Color("a86658");
    }

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
        _ => id
    };

    private static string FormatClassification(ContactClassification classification) => classification switch
    {
        ContactClassification.Tank => "Tank",
        ContactClassification.Recon => "Recon Vehicle",
        ContactClassification.Apc => "APC",
        ContactClassification.CommandVehicle => "Command Vehicle",
        ContactClassification.Vehicle => "Vehicle",
        _ => "Unknown"
    };
}
