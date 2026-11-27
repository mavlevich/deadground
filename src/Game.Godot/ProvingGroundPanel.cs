using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Compact collapsible proving-ground control palette for local sandbox testing:
/// jump camera across the 5 isolated test zones (F1-F5), toggle OPFOR fire stance (F6),
/// and click-to-place friendly or hostile units directly onto the terrain (P).
/// </summary>
internal sealed partial class ProvingGroundPanel : PanelContainer
{
    public enum SpawnSideMode
    {
        SovietPlayer,
        OpforHoldFire,
        OpforReturnFire,
        OpforFireAtWill
    }

    private static readonly (string Label, string UnitId)[] SpawnableUnits =
    [
        ("AK-74", "soviet_rifleman"),
        ("RPG-7V", "soviet_rpg"),
        ("US M16", "us_rifleman"),
        ("T-62", "t62"),
        ("BTR-70", "btr70"),
        ("BRDM-2", "brdm2"),
        ("M60A1", "m60a1"),
        ("M113A1", "m113a1"),
        ("BM-21", "bm21"),
        ("CH-47D", "ch47d"),
    ];

    private readonly VBoxContainer _body;
    private readonly Button _collapseButton;
    private readonly Button _opforStanceButton;
    private readonly Button _placeToggleButton;
    private readonly Label _placementHint;
    private readonly Button[] _sideButtons = new Button[4];
    private readonly Button[] _unitButtons = new Button[SpawnableUnits.Length];

    private bool _collapsed;
    private FireStance _opforStance = FireStance.HoldFire;

    public bool IsPlacementActive { get; private set; }
    public SpawnSideMode SelectedSide { get; private set; } = SpawnSideMode.SovietPlayer;
    public string SelectedUnitId { get; private set; } = "soviet_rpg";

    public event Action<int>? ZoneSelected;
    public event Action<FireStance>? OpforStanceChanged;
    public event Action? ResetRequested;
    public event Action<bool>? PlacementModeChanged;

    public ProvingGroundPanel()
    {
        Name = "ProvingGroundPanel";
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(padding: 8, topRail: true));

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 5);
        AddChild(root);

        HBoxContainer header = new();
        header.AddThemeConstantOverride("separation", 6);
        root.AddChild(header);

        Label title = new()
        {
            Text = "PROVING GROUND [P]",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        title.AddThemeColorOverride("font_color", HudTheme.Accent);
        title.AddThemeFontSizeOverride("font_size", 11);
        header.AddChild(title);

        _collapseButton = new Button
        {
            Text = "-",
            CustomMinimumSize = new Vector2(22, 20),
            FocusMode = FocusModeEnum.None
        };
        _collapseButton.Pressed += () => SetCollapsed(!_collapsed);
        header.AddChild(_collapseButton);

        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 5);
        root.AddChild(_body);

        AddSectionLabel(_body, "ZONES (F1-F5)");
        VBoxContainer zoneBox = new();
        zoneBox.AddThemeConstantOverride("separation", 3);
        _body.AddChild(zoneBox);
        for (int i = 0; i < ProvingGroundScenario.Zones.Length; i++)
        {
            int zoneIdx = i;
            Button zBtn = new()
            {
                Text = ProvingGroundScenario.Zones[i].Name,
                Alignment = HorizontalAlignment.Left,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 22)
            };
            zBtn.AddThemeFontSizeOverride("font_size", 11);
            zBtn.Pressed += () => ZoneSelected?.Invoke(zoneIdx);
            zoneBox.AddChild(zBtn);
        }

        HBoxContainer globalRow = new();
        globalRow.AddThemeConstantOverride("separation", 4);
        _body.AddChild(globalRow);

        _opforStanceButton = new Button
        {
            Text = "OPFOR: Hold [F6]",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 22)
        };
        _opforStanceButton.AddThemeFontSizeOverride("font_size", 10);
        _opforStanceButton.Pressed += CycleOpforStance;
        globalRow.AddChild(_opforStanceButton);

        Button resetBtn = new()
        {
            Text = "Reset",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(52, 22)
        };
        resetBtn.AddThemeFontSizeOverride("font_size", 10);
        resetBtn.Pressed += () => ResetRequested?.Invoke();
        globalRow.AddChild(resetBtn);

        AddSectionLabel(_body, "UNIT SPAWNER");

        GridContainer sideGrid = new() { Columns = 2 };
        sideGrid.AddThemeConstantOverride("h_separation", 4);
        sideGrid.AddThemeConstantOverride("v_separation", 3);
        _body.AddChild(sideGrid);

        string[] sideLabels = ["USSR (P1)", "OPFOR Hold", "OPFOR Ret", "OPFOR Free"];
        for (int i = 0; i < sideLabels.Length; i++)
        {
            SpawnSideMode mode = (SpawnSideMode)i;
            Button btn = new()
            {
                Text = sideLabels[i],
                ToggleMode = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 21)
            };
            btn.AddThemeFontSizeOverride("font_size", 10);
            btn.Pressed += () => SelectSide(mode);
            sideGrid.AddChild(btn);
            _sideButtons[i] = btn;
        }

        GridContainer unitGrid = new() { Columns = 2 };
        unitGrid.AddThemeConstantOverride("h_separation", 4);
        unitGrid.AddThemeConstantOverride("v_separation", 3);
        _body.AddChild(unitGrid);

        for (int i = 0; i < SpawnableUnits.Length; i++)
        {
            int idx = i;
            Button btn = new()
            {
                Text = SpawnableUnits[i].Label,
                ToggleMode = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 21)
            };
            btn.AddThemeFontSizeOverride("font_size", 10);
            btn.Pressed += () => SelectUnit(idx);
            unitGrid.AddChild(btn);
            _unitButtons[i] = btn;
        }

        _placeToggleButton = new Button
        {
            Text = "PLACE ON MAP: OFF [P]",
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 24)
        };
        _placeToggleButton.AddThemeFontSizeOverride("font_size", 11);
        _placeToggleButton.Pressed += () => SetPlacementActive(!IsPlacementActive);
        _body.AddChild(_placeToggleButton);

        _placementHint = new Label
        {
            Text = "Click unit, then LMB terrain (Shift: multi, RMB/Esc: stop)",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _placementHint.AddThemeColorOverride("font_color", HudTheme.Muted);
        _placementHint.AddThemeFontSizeOverride("font_size", 10);
        _body.AddChild(_placementHint);

        RefreshSelections();
    }

    public void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _body.Visible = !_collapsed;
        _collapseButton.Text = _collapsed ? "+" : "-";
    }

    public void CycleOpforStance()
    {
        _opforStance = _opforStance switch
        {
            FireStance.HoldFire => FireStance.ReturnFire,
            FireStance.ReturnFire => FireStance.FireAtWill,
            _ => FireStance.HoldFire
        };
        string label = _opforStance switch
        {
            FireStance.HoldFire => "Hold",
            FireStance.ReturnFire => "Return",
            _ => "Free"
        };
        _opforStanceButton.Text = $"OPFOR: {label} [F6]";
        OpforStanceChanged?.Invoke(_opforStance);
    }

    public void TogglePlacementMode() => SetPlacementActive(!IsPlacementActive);

    public void SetPlacementActive(bool active)
    {
        if (active && _collapsed)
        {
            SetCollapsed(false);
        }
        if (IsPlacementActive == active) return;
        IsPlacementActive = active;
        RefreshSelections();
        PlacementModeChanged?.Invoke(active);
    }

    public void GetSpawnOwnerAndStance(out int owner, out FireStance stance, out float headingRad)
    {
        switch (SelectedSide)
        {
            case SpawnSideMode.SovietPlayer:
                owner = 1;
                stance = FireStance.FireAtWill;
                headingRad = MathF.PI; // face north (+Z)
                break;
            case SpawnSideMode.OpforReturnFire:
                owner = 2;
                stance = FireStance.ReturnFire;
                headingRad = 0f; // face south (-Z)
                break;
            case SpawnSideMode.OpforFireAtWill:
                owner = 2;
                stance = FireStance.FireAtWill;
                headingRad = 0f;
                break;
            default:
                owner = 2;
                stance = FireStance.HoldFire;
                headingRad = 0f;
                break;
        }
    }

    private void SelectSide(SpawnSideMode mode)
    {
        SelectedSide = mode;
        RefreshSelections();
    }

    private void SelectUnit(int index)
    {
        SelectedUnitId = SpawnableUnits[index].UnitId;
        if (!IsPlacementActive)
        {
            SetPlacementActive(true);
        }
        else
        {
            RefreshSelections();
        }
    }

    private void RefreshSelections()
    {
        for (int i = 0; i < _sideButtons.Length; i++)
        {
            _sideButtons[i].SetPressedNoSignal((int)SelectedSide == i);
        }
        for (int i = 0; i < _unitButtons.Length; i++)
        {
            _unitButtons[i].SetPressedNoSignal(SpawnableUnits[i].UnitId == SelectedUnitId);
        }
        _placeToggleButton.SetPressedNoSignal(IsPlacementActive);
        _placeToggleButton.Text = IsPlacementActive
            ? $"PLACING: {SelectedUnitId} [P]"
            : "PLACE ON MAP: OFF [P]";
        _placeToggleButton.AddThemeColorOverride("font_color", IsPlacementActive ? HudTheme.Accent : HudTheme.Text);
    }

    private static void AddSectionLabel(Control parent, string text)
    {
        Label lbl = new() { Text = text };
        lbl.AddThemeColorOverride("font_color", HudTheme.Muted);
        lbl.AddThemeFontSizeOverride("font_size", 10);
        parent.AddChild(lbl);
    }
}
