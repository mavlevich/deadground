using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed partial class MissionPanel : Control
{
    private readonly PanelContainer _briefingCard;
    private readonly Label _headerLabel;
    private readonly Label _objectiveLabel;
    private readonly Label _targetStatusLabel;
    private readonly Label _timerLabel;
    private readonly Label _squadLabel;
    private readonly VBoxContainer _briefingDetails;
    private readonly Label _compactObjectiveLabel;
    private readonly Button _briefingToggleButton;

    private readonly PanelContainer _resultCard;
    private readonly Label _resultTitle;
    private readonly Label _resultReason;
    private readonly Label _resultDuration;
    private readonly Button _retryButton;
    private readonly Button _leaveButton;
    private readonly Label _instructionLabel;

    private readonly HeightMap _map;
    private readonly MeshInstance3D _searchAreaRing;
    private System.Numerics.Vector2 _lastRingCenter;
    private float _lastRingRadius;

    private bool _isLocal;
    private bool _hasFinished;
    private string _scenario = "recon-raid";
    private bool _showBriefing = true;

    public event System.Action? RetryRequested;
    public event System.Action? LeaveRequested;
    public bool IsMissionFinished => _hasFinished;
    internal bool IsBriefingExpanded { get; private set; }
    internal Button BriefingToggleButton => _briefingToggleButton;
    public Rect2 GetBriefingRect() => _briefingCard.Visible ? _briefingCard.GetGlobalRect() : default;
    public Rect2 GetResultRect() => _resultCard.Visible ? _resultCard.GetGlobalRect() : default;
    public Rect2 GetLeaveButtonRect() => _leaveButton.Visible ? _leaveButton.GetGlobalRect() : default;

    public MissionPanel(Node3D parent3D, HeightMap map)
    {
        Theme = HudTheme.Shared;
        _map = map ?? throw new System.ArgumentNullException(nameof(map));
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 1. Briefing and objective HUD card (top left, positioned dynamically via SetBriefingTopOffset)
        _briefingCard = new PanelContainer
        {
            Position = new Vector2(HudLayout.Margin, 72),
            CustomMinimumSize = new Vector2(HudLayout.CardWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _briefingCard.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(padding: 1, topRail: true));

        MarginContainer margin = new() { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);

        VBoxContainer stack = new() { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 3);

        HBoxContainer headingRow = new() { MouseFilter = MouseFilterEnum.Ignore };
        headingRow.AddThemeConstantOverride("separation", 10);
        _briefingToggleButton = new Button
        {
            Text = "OBJECTIVE +",
            ThemeTypeVariation = "CompactButton",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None
        };
        _briefingToggleButton.Pressed += OnBriefingTogglePressed;
        headingRow.AddChild(_briefingToggleButton);
        _timerLabel = new Label
        {
            Text = "Time Remaining: 12:00",
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        HudTheme.ApplyMono(_timerLabel);
        _timerLabel.AddThemeFontSizeOverride("font_size", 12);
        _timerLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        headingRow.AddChild(_timerLabel);
        stack.AddChild(headingRow);

        _compactObjectiveLabel = new Label
        {
            Text = "Destroy enemy command vehicle. Preserve recon vehicle.",
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _compactObjectiveLabel.AddThemeFontSizeOverride("font_size", 12);
        stack.AddChild(_compactObjectiveLabel);

        _briefingDetails = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _briefingDetails.AddThemeConstantOverride("separation", 4);
        stack.AddChild(_briefingDetails);

        _headerLabel = new Label
        {
            Text = "OPERATION: RECON RAID",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _headerLabel.AddThemeFontSizeOverride("font_size", 14);
        _headerLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
        _briefingDetails.AddChild(_headerLabel);

        _objectiveLabel = new Label
        {
            Text = "Primary: Destroy enemy command vehicle. Preserve recon vehicle.",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _objectiveLabel.AddThemeFontSizeOverride("font_size", 13);
        _objectiveLabel.AddThemeColorOverride("font_color", HudTheme.Text);
        _briefingDetails.AddChild(_objectiveLabel);

        _targetStatusLabel = new Label
        {
            Text = "Target: Area ~[420, 420] (Radius 70m) / Unlocated",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _targetStatusLabel.AddThemeFontSizeOverride("font_size", 13);
        _targetStatusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        _briefingDetails.AddChild(_targetStatusLabel);

        _squadLabel = new Label
        {
            Text = "Roster: 1x Recon (70m sensor), 2x Tanks (100mm gun)",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _squadLabel.AddThemeFontSizeOverride("font_size", 12);
        _squadLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        _briefingDetails.AddChild(_squadLabel);

        margin.AddChild(stack);
        _briefingCard.AddChild(margin);
        AddChild(_briefingCard);

        // 2. Center-screen mission result modal panel
        _resultCard = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        _resultCard.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(accented: true, padding: 2, topRail: true, modal: true));

        MarginContainer resultMargin = new() { MouseFilter = MouseFilterEnum.Ignore };
        resultMargin.AddThemeConstantOverride("margin_left", 32);
        resultMargin.AddThemeConstantOverride("margin_right", 32);
        resultMargin.AddThemeConstantOverride("margin_top", 24);
        resultMargin.AddThemeConstantOverride("margin_bottom", 24);

        VBoxContainer resultStack = new() { MouseFilter = MouseFilterEnum.Ignore };
        resultStack.AddThemeConstantOverride("separation", 14);

        _resultTitle = new Label
        {
            Text = "MISSION ACCOMPLISHED",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _resultTitle.AddThemeFontSizeOverride("font_size", 24);
        resultStack.AddChild(_resultTitle);

        _resultReason = new Label
        {
            Text = "Reason description",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _resultReason.AddThemeFontSizeOverride("font_size", 16);
        _resultReason.AddThemeColorOverride("font_color", HudTheme.Text);
        resultStack.AddChild(_resultReason);

        _resultDuration = new Label
        {
            Text = "Duration: 00:00",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _resultDuration.AddThemeFontSizeOverride("font_size", 14);
        _resultDuration.AddThemeColorOverride("font_color", HudTheme.Muted);
        resultStack.AddChild(_resultDuration);

        _retryButton = new Button
        {
            Text = "RETRY MISSION [R]",
            MouseFilter = MouseFilterEnum.Stop
        };
        _retryButton.Pressed += () => RetryRequested?.Invoke();
        resultStack.AddChild(_retryButton);

        _leaveButton = new Button
        {
            Text = "RETURN TO MENU",
            MouseFilter = MouseFilterEnum.Stop
        };
        _leaveButton.Pressed += () => LeaveRequested?.Invoke();
        resultStack.AddChild(_leaveButton);

        _instructionLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _instructionLabel.AddThemeFontSizeOverride("font_size", 13);
        _instructionLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        resultStack.AddChild(_instructionLabel);

        resultMargin.AddChild(resultStack);
        _resultCard.AddChild(resultMargin);
        AddChild(_resultCard);

        // 3. 3D ground search area ring (conforms to terrain heights)
        _searchAreaRing = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(HudTheme.Accent, 0.45f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        parent3D.AddChild(_searchAreaRing);
    }

    public void SetScenario(string scenario)
    {
        _scenario = scenario;
        if (_scenario == "battle")
        {
            _retryButton.Text = "RETRY BATTLE [R]";
            _instructionLabel.Text = "To start the next battle, restart the dedicated server, then return to the menu to reconnect:\nServer: dotnet run --project src/Game.Server --configuration Release -- --scenario battle --port 7777 --max-clients 2";
        }
        else
        {
            _retryButton.Text = "RETRY MISSION [R]";
            _instructionLabel.Text = $"To start the next match, restart the dedicated server, then return to the menu to reconnect:\nServer: dotnet run --project src/Game.Server --configuration Release -- --scenario {_scenario} --port 7777";
        }
    }

    public void SetShowBriefing(bool showBriefing)
    {
        _showBriefing = showBriefing;
        if (!showBriefing)
        {
            _briefingCard.Visible = false;
        }
    }

    public void SetLocalMode(bool isLocal)
    {
        _isLocal = isLocal;
        _retryButton.Visible = isLocal;
        _instructionLabel.Visible = !isLocal;
        _leaveButton.Visible = true;
    }

    public void UpdateMission(in MissionObservation mission, int currentTick)
    {
        if (mission.MissionId == 0)
        {
            _briefingCard.Visible = false;
            _resultCard.Visible = false;
            _searchAreaRing.Visible = false;
            return;
        }

        _briefingCard.Visible = _showBriefing;

        // Position & generate terrain-following search ring
        if (_showBriefing && mission.SearchAreaRadius > 0f)
        {
            UpdateSearchAreaMesh(mission.SearchAreaCenter, mission.SearchAreaRadius);
            _searchAreaRing.Visible = true;
        }
        else
        {
            _searchAreaRing.Visible = false;
        }

        int effectiveTick = (mission.Status is MissionStatus.Victory or MissionStatus.Defeat or MissionStatus.Draw && mission.FinishedTick > 0)
            ? mission.FinishedTick
            : currentTick;

        if (_showBriefing)
        {
            // Timer
            int remainingTicks = Math.Max(0, mission.TimeLimitTicks - effectiveTick);
            int remainingSeconds = remainingTicks / Simulation.TickRate;
            int remMins = remainingSeconds / 60;
            int remSecs = remainingSeconds % 60;
            _timerLabel.Text = $"Time Remaining: {remMins:D2}:{remSecs:D2}";

            // Target status
            if (mission.IsTargetDestroyed)
            {
                _targetStatusLabel.Text = "Target: DESTROYED (Objective complete)";
                _targetStatusLabel.AddThemeColorOverride("font_color", HudTheme.Accent);
            }
            else if (mission.IsTargetSpotted && mission.TargetContactId.Value != 0)
            {
                _targetStatusLabel.Text = $"Target: SPOTTED (Enemy Contact #{mission.TargetContactId.Value})";
                _targetStatusLabel.AddThemeColorOverride("font_color", HudTheme.Danger);
            }
            else
            {
                _targetStatusLabel.Text = $"Target Area: ~[{mission.SearchAreaCenter.X:F0}, {mission.SearchAreaCenter.Y:F0}] (Radius {mission.SearchAreaRadius:F0}m) / Unlocated";
                _targetStatusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
            }
        }

        // Result presentation
        if (mission.Status is MissionStatus.Victory or MissionStatus.Defeat or MissionStatus.Draw)
        {
            _hasFinished = true;
            _resultCard.Visible = true;
            _retryButton.Visible = _isLocal;
            _instructionLabel.Visible = !_isLocal;

            int elapsedSeconds = effectiveTick / Simulation.TickRate;
            int elMins = elapsedSeconds / 60;
            int elSecs = elapsedSeconds % 60;
            _resultDuration.Text = _scenario == "battle"
                ? $"Battle Duration: {elMins:D2}:{elSecs:D2}"
                : $"Attempt Duration: {elMins:D2}:{elSecs:D2}";

            if (mission.Status == MissionStatus.Victory)
            {
                _resultTitle.Text = "VICTORY";
                _resultTitle.AddThemeColorOverride("font_color", HudTheme.Accent);
                _resultReason.Text = _scenario == "battle"
                    ? "All enemy units destroyed."
                    : "Enemy command vehicle was located and destroyed.";
            }
            else if (mission.Status == MissionStatus.Draw)
            {
                _resultTitle.Text = "DRAW";
                _resultTitle.AddThemeColorOverride("font_color", HudTheme.Accent);
                _resultReason.Text = "Mutual destruction: all units on both sides destroyed.";
            }
            else
            {
                _resultTitle.Text = "DEFEAT";
                _resultTitle.AddThemeColorOverride("font_color", HudTheme.Danger);
                if (_scenario == "battle")
                {
                    _resultReason.Text = "All friendly units destroyed.";
                }
                else
                {
                    _resultReason.Text = mission.EndReason switch
                    {
                        MissionEndReason.ReconVehicleDestroyed => "Your reconnaissance vehicle was destroyed.",
                        MissionEndReason.AllArmedUnitsDestroyed => "All combat tanks in your unit were destroyed.",
                        MissionEndReason.TimeLimitExpired => "Mission time limit expired (12:00).",
                        _ => "Mission objective failed."
                    };
                }
            }
            HudTheme.CenterModal(_resultCard);
        }
        else
        {
            _hasFinished = false;
            _resultCard.Visible = false;
        }
    }

    private void UpdateSearchAreaMesh(System.Numerics.Vector2 center, float radius)
    {
        if (_lastRingCenter == center && MathF.Abs(_lastRingRadius - radius) < 0.1f)
            return;

        _lastRingCenter = center;
        _lastRingRadius = radius;

        if (radius <= 0f)
        {
            _searchAreaRing.Visible = false;
            return;
        }

        const int segments = 64;
        float innerR = MathF.Max(0.5f, radius - 0.8f);
        float outerR = radius + 0.8f;

        int vertCount = (segments + 1) * 2;
        Vector3[] vertices = new Vector3[vertCount];
        int[] indices = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float angle = (i % segments) * (MathF.PI * 2f / segments);
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            float inX = center.X + innerR * cos;
            float inZ = center.Y + innerR * sin;
            float inY = _map.GetHeight(inX, inZ) + 0.2f;

            float outX = center.X + outerR * cos;
            float outZ = center.Y + outerR * sin;
            float outY = _map.GetHeight(outX, outZ) + 0.2f;

            int vIdx = i * 2;
            vertices[vIdx] = new Vector3(inX, inY, inZ);
            vertices[vIdx + 1] = new Vector3(outX, outY, outZ);

            if (i < segments)
            {
                int idx = i * 6;
                int currentIn = vIdx;
                int currentOut = vIdx + 1;
                int nextIn = vIdx + 2;
                int nextOut = vIdx + 3;

                indices[idx] = currentIn;
                indices[idx + 1] = currentOut;
                indices[idx + 2] = nextIn;

                indices[idx + 3] = nextIn;
                indices[idx + 4] = currentOut;
                indices[idx + 5] = nextOut;
            }
        }

        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _searchAreaRing.Mesh = mesh;
    }

    public void SetBriefingTopOffset(float topY)
    {
        float targetY = MathF.Max(24f, topY);
        if (MathF.Abs(_briefingCard.Position.Y - targetY) > 0.5f)
        {
            _briefingCard.Position = new Vector2(_briefingCard.Position.X, targetY);
        }
    }

    public void SetBriefing(string title, string objective, string roster)
    {
        _headerLabel.Text = title;
        _objectiveLabel.Text = objective;
        _compactObjectiveLabel.Text = objective.StartsWith("Primary: ", StringComparison.Ordinal)
            ? objective["Primary: ".Length..]
            : objective;
        _squadLabel.Text = roster;
        _briefingCard.ResetSize();
    }

    internal void SetBriefingExpanded(bool expanded)
    {
        IsBriefingExpanded = expanded;
        _briefingDetails.Visible = expanded;
        _compactObjectiveLabel.Visible = !expanded;
        _briefingToggleButton.Text = expanded ? "OBJECTIVE -" : "OBJECTIVE +";
        _briefingCard.ResetSize();
    }

    private void OnBriefingTogglePressed() => SetBriefingExpanded(!IsBriefingExpanded);

    public void ResetUi()
    {
        SetBriefingExpanded(false);
        _hasFinished = false;
        _resultCard.Visible = false;
        _targetStatusLabel.Text = "Target: Locating...";
        _targetStatusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        _timerLabel.Text = "Time Remaining: 12:00";
    }

    public void Cleanup()
    {
        _searchAreaRing.QueueFree();
    }
}
