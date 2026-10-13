using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed partial class MissionPanel : Control
{
    private readonly PanelContainer _briefingCard;
    private readonly Label _objectiveLabel;
    private readonly Label _targetStatusLabel;
    private readonly Label _timerLabel;
    private readonly Label _squadLabel;

    private readonly PanelContainer _resultCard;
    private readonly Label _resultTitle;
    private readonly Label _resultReason;
    private readonly Label _resultDuration;
    private readonly Button _retryButton;

    private readonly HeightMap _map;
    private readonly MeshInstance3D _searchAreaRing;
    private System.Numerics.Vector2 _lastRingCenter;
    private float _lastRingRadius;

    private bool _isLocal;
    private bool _hasFinished;

    public event System.Action? RetryRequested;
    public bool IsMissionFinished => _hasFinished;

    public MissionPanel(Node3D parent3D, HeightMap map)
    {
        _map = map ?? throw new System.ArgumentNullException(nameof(map));
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 1. Briefing and objective HUD card (top left, below title)
        _briefingCard = new PanelContainer
        {
            Position = new Vector2(24, 156),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _briefingCard.AddThemeStyleboxOverride("panel", HudCardStyle());

        MarginContainer margin = new() { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        VBoxContainer stack = new() { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 4);

        Label header = new()
        {
            Text = "OPERATION: RECON RAID",
            MouseFilter = MouseFilterEnum.Ignore
        };
        header.AddThemeFontSizeOverride("font_size", 14);
        header.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(header);

        _objectiveLabel = new Label
        {
            Text = "Primary: Destroy enemy command vehicle. Preserve recon vehicle.",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _objectiveLabel.AddThemeFontSizeOverride("font_size", 13);
        _objectiveLabel.AddThemeColorOverride("font_color", new Color("d0ded0"));
        stack.AddChild(_objectiveLabel);

        _targetStatusLabel = new Label
        {
            Text = "Target: Area ~[420, 420] (Radius 70m) / Unlocated",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _targetStatusLabel.AddThemeFontSizeOverride("font_size", 13);
        _targetStatusLabel.AddThemeColorOverride("font_color", new Color("a8b8a0"));
        stack.AddChild(_targetStatusLabel);

        _timerLabel = new Label
        {
            Text = "Time Remaining: 12:00",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _timerLabel.AddThemeFontSizeOverride("font_size", 13);
        _timerLabel.AddThemeColorOverride("font_color", new Color("f0e0a0"));
        stack.AddChild(_timerLabel);

        _squadLabel = new Label
        {
            Text = "Roster: 1x Recon (70m sensor), 2x Tanks (100mm gun)",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _squadLabel.AddThemeFontSizeOverride("font_size", 12);
        _squadLabel.AddThemeColorOverride("font_color", new Color("8a9a88"));
        stack.AddChild(_squadLabel);

        margin.AddChild(stack);
        _briefingCard.AddChild(margin);
        AddChild(_briefingCard);

        // 2. Center-screen mission result modal panel
        _resultCard = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        _resultCard.AddThemeStyleboxOverride("panel", ResultCardStyle());

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
        _resultReason.AddThemeColorOverride("font_color", new Color("d5ded0"));
        resultStack.AddChild(_resultReason);

        _resultDuration = new Label
        {
            Text = "Duration: 00:00",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _resultDuration.AddThemeFontSizeOverride("font_size", 14);
        _resultDuration.AddThemeColorOverride("font_color", new Color("b0c0a8"));
        resultStack.AddChild(_resultDuration);

        _retryButton = new Button
        {
            Text = "RETRY MISSION [R]",
            MouseFilter = MouseFilterEnum.Stop
        };
        _retryButton.Pressed += () => RetryRequested?.Invoke();
        resultStack.AddChild(_retryButton);

        resultMargin.AddChild(resultStack);
        _resultCard.AddChild(resultMargin);
        AddChild(_resultCard);

        // 3. 3D ground search area ring (conforms to terrain heights)
        _searchAreaRing = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.93f, 0.78f, 0.42f, 0.45f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        parent3D.AddChild(_searchAreaRing);
    }

    public void SetLocalMode(bool isLocal)
    {
        _isLocal = isLocal;
        _retryButton.Visible = isLocal;
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

        _briefingCard.Visible = true;

        // Position & generate terrain-following search ring
        if (mission.SearchAreaRadius > 0f)
        {
            UpdateSearchAreaMesh(mission.SearchAreaCenter, mission.SearchAreaRadius);
            _searchAreaRing.Visible = true;
        }
        else
        {
            _searchAreaRing.Visible = false;
        }

        int effectiveTick = (mission.Status is MissionStatus.Victory or MissionStatus.Defeat && mission.FinishedTick > 0)
            ? mission.FinishedTick
            : currentTick;

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
            _targetStatusLabel.AddThemeColorOverride("font_color", new Color("77dd77"));
        }
        else if (mission.IsTargetIdentified && mission.TargetContactId.Value != 0)
        {
            _targetStatusLabel.Text = $"Target: IDENTIFIED (Enemy Contact #{mission.TargetContactId.Value})";
            _targetStatusLabel.AddThemeColorOverride("font_color", new Color("ff7777"));
        }
        else
        {
            _targetStatusLabel.Text = $"Target Area: ~[{mission.SearchAreaCenter.X:F0}, {mission.SearchAreaCenter.Y:F0}] (Radius {mission.SearchAreaRadius:F0}m) / Unlocated";
            _targetStatusLabel.AddThemeColorOverride("font_color", new Color("a8b8a0"));
        }

        // Result presentation
        if (mission.Status is MissionStatus.Victory or MissionStatus.Defeat)
        {
            _hasFinished = true;
            _resultCard.Visible = true;

            // Center result card
            Vector2 viewportSize = GetViewportRect().Size;
            Vector2 cardSize = _resultCard.GetCombinedMinimumSize();
            _resultCard.Position = new Vector2(
                (viewportSize.X - cardSize.X) * 0.5f,
                (viewportSize.Y - cardSize.Y) * 0.5f);

            int elapsedSeconds = effectiveTick / Simulation.TickRate;
            int elMins = elapsedSeconds / 60;
            int elSecs = elapsedSeconds % 60;
            _resultDuration.Text = $"Attempt Duration: {elMins:D2}:{elSecs:D2}";

            if (mission.Status == MissionStatus.Victory)
            {
                _resultTitle.Text = "VICTORY";
                _resultTitle.AddThemeColorOverride("font_color", new Color("77dd77"));
                _resultReason.Text = "Enemy command vehicle was located and destroyed.";
            }
            else
            {
                _resultTitle.Text = "DEFEAT";
                _resultTitle.AddThemeColorOverride("font_color", new Color("ee6666"));
                _resultReason.Text = mission.EndReason switch
                {
                    MissionEndReason.ReconVehicleDestroyed => "Your reconnaissance vehicle was destroyed.",
                    MissionEndReason.AllArmedUnitsDestroyed => "All combat tanks in your unit were destroyed.",
                    MissionEndReason.TimeLimitExpired => "Mission time limit expired (12:00).",
                    _ => "Mission objective failed."
                };
            }
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

    public void ResetUi()
    {
        _hasFinished = false;
        _resultCard.Visible = false;
        _targetStatusLabel.Text = "Target: Area ~[420, 420] (Radius 70m) / Unlocated";
        _targetStatusLabel.AddThemeColorOverride("font_color", new Color("a8b8a0"));
        _timerLabel.Text = "Time Remaining: 12:00";
    }

    private static StyleBoxFlat HudCardStyle() => new()
    {
        BgColor = new Color(0.045f, 0.07f, 0.055f, 0.92f),
        BorderColor = new Color("3b4835"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 5,
        CornerRadiusBottomRight = 5,
        CornerRadiusTopLeft = 5,
        CornerRadiusTopRight = 5
    };

    private static StyleBoxFlat ResultCardStyle() => new()
    {
        BgColor = new Color(0.04f, 0.06f, 0.05f, 0.96f),
        BorderColor = new Color("edc76b"),
        BorderWidthBottom = 2,
        BorderWidthTop = 2,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8
    };

    public void Cleanup()
    {
        _searchAreaRing.QueueFree();
    }
}
