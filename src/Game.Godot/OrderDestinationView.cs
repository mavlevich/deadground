using Godot;

namespace DeadGround.Client;

public enum OrderMarkerVisualState : byte
{
    Hidden = 0,
    Sent = 1,
    Processing = 2,
    PathFound = 3,
    Failed = 4,
    Partial = 5
}

/// <summary>
/// 3D destination marker showing order status (sent, route searching, path found, failed, partial).
/// Includes ghost preview ring for adjusted target positions.
/// Allocation-free during per-frame updates.
/// </summary>
internal sealed partial class OrderDestinationView : Node3D
{
    private readonly Node3D _markerRoot;
    private readonly MeshInstance3D _ring;
    private readonly MeshInstance3D _centerDot;
    private readonly MeshInstance3D _ghostRing;

    private readonly StandardMaterial3D _matSent;
    private readonly StandardMaterial3D _matProcessing;
    private readonly StandardMaterial3D _matSuccess;
    private readonly StandardMaterial3D _matFailed;
    private readonly StandardMaterial3D _matPartial;
    private readonly StandardMaterial3D _matGhost;

    private OrderMarkerVisualState _state = OrderMarkerVisualState.Hidden;
    private double _timeInState;
    private Vector3 _targetPosition;

    public OrderMarkerVisualState CurrentState => _state;
    public Vector3 TargetPosition => _targetPosition;
    internal bool IsGhostVisible => _ghostRing.Visible;
    internal bool IsGhostVisibleInTree => _ghostRing.IsVisibleInTree();
    internal bool IsSubmittedMarkerVisible => _markerRoot.Visible;
    internal bool IsSubmittedMarkerVisibleInTree => _markerRoot.IsVisibleInTree();
    internal Vector3 GhostPosition => _ghostRing.Position;

    public OrderDestinationView()
    {
        _matSent = CreateUnshadedMaterial(new Color("edc76b"));        // Amber
        _matProcessing = CreateUnshadedMaterial(new Color("4da6ff"));  // Cyan
        _matSuccess = CreateUnshadedMaterial(new Color("5cb85c"));     // Green
        _matFailed = CreateUnshadedMaterial(new Color("ff4d4d"));      // Red
        _matPartial = CreateUnshadedMaterial(new Color("f0ad4e"));     // Orange / amber

        _matGhost = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.93f, 0.78f, 0.42f, 0.5f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        TorusMesh ringMesh = new()
        {
            InnerRadius = 1.35f,
            OuterRadius = 1.65f,
            Rings = 24,
            RingSegments = 8
        };

        CylinderMesh dotMesh = new()
        {
            TopRadius = 0.25f,
            BottomRadius = 0.25f,
            Height = 0.08f
        };

        TorusMesh ghostMesh = new()
        {
            InnerRadius = 1.0f,
            OuterRadius = 1.25f,
            Rings = 20,
            RingSegments = 8
        };

        _markerRoot = new Node3D { Name = "SubmittedMarkerRoot", Visible = false };
        AddChild(_markerRoot);

        _ring = new MeshInstance3D
        {
            Mesh = ringMesh,
            Scale = new Vector3(1f, 0.15f, 1f),
            MaterialOverride = _matSent
        };
        _markerRoot.AddChild(_ring);

        _centerDot = new MeshInstance3D
        {
            Mesh = dotMesh,
            MaterialOverride = _matSent
        };
        _markerRoot.AddChild(_centerDot);

        _ghostRing = new MeshInstance3D
        {
            Mesh = ghostMesh,
            Scale = new Vector3(1f, 0.15f, 1f),
            MaterialOverride = _matGhost,
            Visible = false
        };
        AddChild(_ghostRing);

        Visible = true;
    }

    private static StandardMaterial3D CreateUnshadedMaterial(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };

    public void Show(Vector3 worldPos, OrderMarkerVisualState state)
    {
        _targetPosition = worldPos;
        _markerRoot.Position = worldPos;
        _state = state;
        _timeInState = 0;
        _markerRoot.Visible = state != OrderMarkerVisualState.Hidden;
        ApplyMaterialForState(state);
    }

    public void SetState(OrderMarkerVisualState state)
    {
        if (_state == state) return;
        _state = state;
        _timeInState = 0;
        _markerRoot.Visible = state != OrderMarkerVisualState.Hidden;
        ApplyMaterialForState(state);
    }

    private void ApplyMaterialForState(OrderMarkerVisualState state)
    {
        StandardMaterial3D mat = state switch
        {
            OrderMarkerVisualState.Sent => _matSent,
            OrderMarkerVisualState.Processing => _matProcessing,
            OrderMarkerVisualState.PathFound => _matSuccess,
            OrderMarkerVisualState.Failed => _matFailed,
            OrderMarkerVisualState.Partial => _matPartial,
            _ => _matSent
        };
        _ring.MaterialOverride = mat;
        _centerDot.MaterialOverride = mat;
    }

    public void ShowGhost(Vector3 worldPos)
    {
        _ghostRing.Position = worldPos;
        _ghostRing.Visible = true;
    }

    public void HideGhost()
    {
        _ghostRing.Visible = false;
    }

    public void Update(double delta)
    {
        if (!_markerRoot.Visible && !_ghostRing.Visible) return;

        _timeInState += delta;

        if (_markerRoot.Visible)
        {
            switch (_state)
            {
                case OrderMarkerVisualState.Sent:
                    {
                        float pulse = 1.0f + 0.08f * MathF.Sin((float)_timeInState * 8.0f);
                        _ring.Scale = new Vector3(pulse, 0.15f, pulse);
                        break;
                    }
                case OrderMarkerVisualState.Processing:
                    {
                        float pulse = 1.0f + 0.14f * MathF.Sin((float)_timeInState * 10.0f);
                        _ring.Scale = new Vector3(pulse, 0.15f, pulse);
                        break;
                    }
                case OrderMarkerVisualState.PathFound:
                    {
                        float pulse = 1.0f + 0.03f * MathF.Sin((float)_timeInState * 3.0f);
                        _ring.Scale = new Vector3(pulse, 0.15f, pulse);
                        if (_timeInState > 3.0)
                        {
                            float fade = (float)Math.Clamp(1.0 - (_timeInState - 3.0) / 1.5, 0.0, 1.0);
                            if (fade <= 0f)
                            {
                                ClearSubmittedMarker();
                            }
                        }
                        break;
                    }
                case OrderMarkerVisualState.Failed:
                    {
                        // Flash red 3 times in 1.2s, then fade out
                        float flash = (MathF.Sin((float)_timeInState * 16.0f) > 0f) ? 1.0f : 0.35f;
                        _ring.Scale = new Vector3(1.1f, 0.15f, 1.1f);
                        if (_timeInState > 1.2)
                        {
                            float fade = (float)Math.Clamp(1.0 - (_timeInState - 1.2) / 0.8, 0.0, 1.0);
                            if (fade <= 0f)
                            {
                                ClearSubmittedMarker();
                            }
                        }
                        break;
                    }
                case OrderMarkerVisualState.Partial:
                    {
                        float pulse = 1.0f + 0.06f * MathF.Sin((float)_timeInState * 5.0f);
                        _ring.Scale = new Vector3(pulse, 0.15f, pulse);
                        if (_timeInState > 4.0)
                        {
                            float fade = (float)Math.Clamp(1.0 - (_timeInState - 4.0) / 1.5, 0.0, 1.0);
                            if (fade <= 0f)
                            {
                                ClearSubmittedMarker();
                            }
                        }
                        break;
                    }
            }
        }

        if (_ghostRing.Visible)
        {
            float ghostPulse = 1.0f + 0.05f * MathF.Sin((float)_timeInState * 6.0f);
            _ghostRing.Scale = new Vector3(ghostPulse, 0.15f, ghostPulse);
        }
    }

    public void ClearSubmittedMarker()
    {
        _markerRoot.Visible = false;
        _state = OrderMarkerVisualState.Hidden;
        _timeInState = 0;
    }

    public void Clear()
    {
        ClearSubmittedMarker();
        HideGhost();
    }
}
