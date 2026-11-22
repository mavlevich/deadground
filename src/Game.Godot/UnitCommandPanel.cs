using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

internal enum HudTargetingMode { None, Move, Reverse, Attack }

// Buttons expose existing commands. The server still decides whether an order succeeds.
internal sealed partial class UnitCommandPanel : PanelContainer
{
    internal Button MoveButton { get; }
    internal Button ReverseButton { get; }
    internal Button AttackButton { get; }
    internal Button StopButton { get; }
    internal Button RangeButton { get; }
    private readonly Button[] _stanceButtons;
    private readonly Label _heading;
    private UnitCatalog _catalog = null!;
    private IUnitMovementCatalog _movementCatalog = null!;
    private int _lastCount = -1;
    private bool _lastMixedStance;
    private HudTargetingMode _mode = (HudTargetingMode)(-1);
    public event Action<HudTargetingMode>? TargetRequested;
    public event Action<FireStance>? StanceRequested;
    public event Action? StopRequested;
    public event Action? RangeToggleRequested;

    public UnitCommandPanel(UnitCatalog catalog)
    {
        Theme = HudTheme.Shared;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(HudLayout.CommandWidth, HudLayout.CommandHeight);
        AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(padding: 8, topRail: true));

        VBoxContainer column = new() { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 5);
        AddChild(column);

        _heading = new Label { Text = "COMMAND / NO SELECTION", MouseFilter = MouseFilterEnum.Ignore };
        HudTheme.ApplyMono(_heading);
        _heading.AddThemeFontSizeOverride("font_size", 10);
        _heading.AddThemeColorOverride("font_color", HudTheme.Muted);

        HBoxContainer headingRow = new() { MouseFilter = MouseFilterEnum.Ignore };
        headingRow.AddThemeConstantOverride("separation", 5);
        _heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headingRow.AddChild(_heading);

        StopButton = new Button
        {
            Text = "STOP [E]",
            ThemeTypeVariation = "CompactButton",
            FocusMode = FocusModeEnum.None,
            TooltipText = "E: stop selected units and clear active move, attack, boarding, or barrage orders."
        };
        StopButton.Pressed += OnStop;
        headingRow.AddChild(StopButton);

        RangeButton = new Button
        {
            Text = "RANGES [F7]",
            ThemeTypeVariation = "CompactButton",
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = "F7: toggle selected unit range limits. Solid = weapon; dashed = sensor. Obstacles can block both."
        };
        RangeButton.Pressed += OnRangeToggle;
        headingRow.AddChild(RangeButton);
        column.AddChild(headingRow);

        GridContainer grid = new() { Columns = 3, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 5);
        grid.AddThemeConstantOverride("v_separation", 5);
        column.AddChild(grid);

        MoveButton = CreateButton("MOVE", "Choose a destination. Right-drag on terrain to also choose final facing.", toggle: true);
        ReverseButton = CreateButton("REVERSE [R]", "Choose a destination for reverse movement. Keyboard alternative: R + right-click.", toggle: true);
        AttackButton = CreateButton("ATTACK", "Choose an observed enemy contact. This is not an attack-ground command.", toggle: true);
        MoveButton.Pressed += OnMove;
        ReverseButton.Pressed += OnReverse;
        AttackButton.Pressed += OnAttack;
        grid.AddChild(MoveButton);
        grid.AddChild(ReverseButton);
        grid.AddChild(AttackButton);

        _stanceButtons =
        [
            CreateButton("FIRE AT WILL [Z]", "Engage targets automatically.", toggle: true),
            CreateButton("RETURN FIRE [X]", "Use the existing return-fire stance.", toggle: true),
            CreateButton("HOLD FIRE [C]", "Do not engage automatically.", toggle: true)
        ];
        _stanceButtons[0].Pressed += OnFireAtWill;
        _stanceButtons[1].Pressed += OnReturnFire;
        _stanceButtons[2].Pressed += OnHoldFire;
        foreach (Button button in _stanceButtons)
        {
            grid.AddChild(button);
        }
        SetCatalog(catalog);
    }

    internal void SetCatalog(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _movementCatalog = new OpsMovementCatalog(catalog);
        _lastCount = -1;
        _lastMixedStance = false;
        _mode = (HudTargetingMode)(-1);
        SetTargetingMode(HudTargetingMode.None);
        SetAvailable(false, false, false, false, false, 0);
    }

    private static Button CreateButton(string text, string tooltip, bool toggle = false)
    {
        Button button = new()
        {
            Text = text,
            TooltipText = tooltip,
            ToggleMode = toggle,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None
        };
        HudTheme.ApplyMono(button);
        button.AddThemeFontSizeOverride("font_size", 10);
        return button;
    }

    public void UpdateSelection(HashSet<EntityId> selected, ClientReplica replica, bool finished)
    {
        bool move = false, reverse = false, attack = false, armed = false;
        int stanceMask = 0, count = 0;
        foreach (EntityId id in selected)
        {
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation own) || own.ContainerId.Value != 0 || own.Health == 0 || own.Definition >= _catalog.Units.Length) continue;
            count++;
            UnitDefinition def = _catalog.Units[own.Definition];
            if (_movementCatalog.TryGetProfile(own.Definition, out UnitMovementProfile movement))
            {
                move |= movement.Speed > 0;
                reverse |= movement.ReverseSpeed > 0;
            }
            armed |= def.Weapon is not null;
            attack |= def.Weapon is not null && own.Ammunition > 0;
            stanceMask |= 1 << (int)own.Stance;
        }
        bool mixedStance = stanceMask != 0 && (stanceMask & (stanceMask - 1)) != 0;
        if (_lastCount != count || _lastMixedStance != mixedStance)
        {
            _lastCount = count;
            _lastMixedStance = mixedStance;
            UpdateHeading();
        }
        SetAvailable(!finished && move, !finished && reverse, !finished && attack, !finished && armed, !finished && count > 0, stanceMask);
    }

    private void SetAvailable(bool move, bool reverse, bool attack, bool armed, bool stop, int stanceMask)
    {
        MoveButton.Disabled = !move;
        ReverseButton.Disabled = !reverse;
        AttackButton.Disabled = !attack;
        StopButton.Disabled = !stop;
        for (int i = 0; i < _stanceButtons.Length; i++)
        {
            _stanceButtons[i].Disabled = !armed;
            _stanceButtons[i].SetPressedNoSignal(stanceMask == 1 << i);
        }
    }

    public void SetTargetingMode(HudTargetingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        MoveButton.SetPressedNoSignal(mode == HudTargetingMode.Move);
        ReverseButton.SetPressedNoSignal(mode == HudTargetingMode.Reverse);
        AttackButton.SetPressedNoSignal(mode == HudTargetingMode.Attack);

        string hint = mode switch
        {
            HudTargetingMode.Move => "MOVE / LEFT-CLICK TERRAIN / ESC OR RIGHT-CLICK TO CANCEL",
            HudTargetingMode.Reverse => "REVERSE / LEFT-CLICK TERRAIN / ESC OR RIGHT-CLICK TO CANCEL",
            HudTargetingMode.Attack => "ATTACK / LEFT-CLICK ENEMY CONTACT / ESC OR RIGHT-CLICK TO CANCEL",
            _ => "RANGES: SOLID = WEAPON / DASHED = SENSOR LIMIT; LOS MAY BLOCK"
        };
        TooltipText = hint;
        _heading.AddThemeColorOverride("font_color", mode == HudTargetingMode.None ? HudTheme.Muted : HudTheme.Accent);
        UpdateHeading();
    }

    public void SetRangesVisible(bool visible) => RangeButton.SetPressedNoSignal(visible);

    private void UpdateHeading() => _heading.Text = _mode switch
    {
        HudTargetingMode.Move => "MOVE / LMB TERRAIN / ESC CANCEL",
        HudTargetingMode.Reverse => "REVERSE / LMB TERRAIN / ESC CANCEL",
        HudTargetingMode.Attack => "ATTACK / LMB CONTACT / ESC CANCEL",
        _ => _lastCount <= 0
            ? "COMMAND / NO SELECTION"
            : _lastMixedStance
                ? $"COMMAND / {_lastCount} SELECTED / MIXED"
                : $"COMMAND / {_lastCount} SELECTED"
    };

    private void RequestTarget(HudTargetingMode requested)
    {
        HudTargetingMode next = _mode == requested ? HudTargetingMode.None : requested;
        TargetRequested?.Invoke(next);
    }

    private void OnMove() => RequestTarget(HudTargetingMode.Move);
    private void OnReverse() => RequestTarget(HudTargetingMode.Reverse);
    private void OnAttack() => RequestTarget(HudTargetingMode.Attack);
    private void OnFireAtWill() => StanceRequested?.Invoke(FireStance.FireAtWill);
    private void OnReturnFire() => StanceRequested?.Invoke(FireStance.ReturnFire);
    private void OnHoldFire() => StanceRequested?.Invoke(FireStance.HoldFire);
    private void OnStop() => StopRequested?.Invoke();
    private void OnRangeToggle() => RangeToggleRequested?.Invoke();
}
