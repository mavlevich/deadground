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
    private readonly Node3D _facingArrowRoot;
    private readonly MeshInstance3D _facingArrowShaft;
    private readonly MeshInstance3D _facingArrowHead;

    private readonly StandardMaterial3D _matSent;
    private readonly StandardMaterial3D _matProcessing;
    private readonly StandardMaterial3D _matSuccess;
    private readonly StandardMaterial3D _matFailed;
    private readonly StandardMaterial3D _matPartial;
    private readonly StandardMaterial3D _matGhost;

    private OrderMarkerVisualState _state = OrderMarkerVisualState.Hidden;
    private double _timeInState;
    private Vector3 _targetPosition;

    public const int MaxGroupSlots = 128;

    internal sealed class PooledMarker
    {
        public readonly Node3D Root;
        public readonly MeshInstance3D Ring;
        public readonly MeshInstance3D CenterDot;
        public readonly MeshInstance3D GhostRing;
        public readonly Node3D FacingArrowRoot;
        public readonly MeshInstance3D FacingArrowShaft;
        public readonly MeshInstance3D FacingArrowHead;

        public OrderMarkerVisualState State = OrderMarkerVisualState.Hidden;
        public double TimeInState;
        public bool IsActive;

        public PooledMarker(
            Node3D parent,
            TorusMesh ringMesh,
            CylinderMesh dotMesh,
            TorusMesh ghostMesh,
            BoxMesh shaftMesh,
            CylinderMesh headMesh,
            StandardMaterial3D initialMat,
            StandardMaterial3D ghostMat)
        {
            Root = new Node3D { Visible = false };
            parent.AddChild(Root);

            Ring = new MeshInstance3D
            {
                Mesh = ringMesh,
                Scale = new Vector3(1f, 0.15f, 1f),
                MaterialOverride = initialMat
            };
            Root.AddChild(Ring);

            CenterDot = new MeshInstance3D
            {
                Mesh = dotMesh,
                MaterialOverride = initialMat
            };
            Root.AddChild(CenterDot);

            GhostRing = new MeshInstance3D
            {
                Mesh = ghostMesh,
                Scale = new Vector3(1f, 0.15f, 1f),
                MaterialOverride = ghostMat,
                Visible = false
            };
            Root.AddChild(GhostRing);

            FacingArrowRoot = new Node3D { Visible = false };
            Root.AddChild(FacingArrowRoot);

            FacingArrowShaft = new MeshInstance3D
            {
                Mesh = shaftMesh,
                Position = new Vector3(0f, 0.05f, -1.0f),
                MaterialOverride = initialMat
            };
            FacingArrowRoot.AddChild(FacingArrowShaft);

            FacingArrowHead = new MeshInstance3D
            {
                Mesh = headMesh,
                Rotation = new Vector3(-Mathf.Pi / 2f, 0f, 0f),
                Position = new Vector3(0f, 0.05f, -2.4f),
                MaterialOverride = initialMat
            };
            FacingArrowRoot.AddChild(FacingArrowHead);
        }

        public void ApplyMaterial(StandardMaterial3D mat)
        {
            Ring.MaterialOverride = mat;
            CenterDot.MaterialOverride = mat;
            FacingArrowShaft.MaterialOverride = mat;
            FacingArrowHead.MaterialOverride = mat;
        }

        public void Hide()
        {
            Root.Visible = false;
            GhostRing.Visible = false;
            FacingArrowRoot.Visible = false;
            IsActive = false;
            State = OrderMarkerVisualState.Hidden;
            TimeInState = 0;
        }
    }

    private readonly PooledMarker[] _markerPool = new PooledMarker[MaxGroupSlots];
    private int _activeGroupSlotCount;
    public int ActiveGroupSlotCount => _activeGroupSlotCount;

    private readonly PooledMarker[] _previewPool = new PooledMarker[MaxGroupSlots];
    private int _activePreviewSlotCount;
    public int ActivePreviewSlotCount => _activePreviewSlotCount;

    public OrderMarkerVisualState CurrentState => _state;
    public Vector3 TargetPosition => _targetPosition;
    internal bool IsGhostVisible => _ghostRing.Visible;
    internal bool IsGhostVisibleInTree => _ghostRing.IsVisibleInTree();
    internal bool IsSubmittedMarkerVisible => _markerRoot.Visible;
    internal bool IsSubmittedMarkerVisibleInTree => _markerRoot.IsVisibleInTree();
    internal Vector3 GhostPosition => _ghostRing.Position;
    internal bool IsFacingArrowVisible => _facingArrowRoot.Visible;
    internal bool IsFacingArrowVisibleInTree => _facingArrowRoot.IsVisibleInTree();
    internal float FacingArrowYaw => _facingArrowRoot.Rotation.Y;
    internal Vector3 FacingArrowPosition => _facingArrowRoot.Position;

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

        _facingArrowRoot = new Node3D { Name = "FacingArrowRoot", Visible = false };
        AddChild(_facingArrowRoot);

        BoxMesh shaftMesh = new()
        {
            Size = new Vector3(0.24f, 0.08f, 2.0f)
        };
        _facingArrowShaft = new MeshInstance3D
        {
            Mesh = shaftMesh,
            Position = new Vector3(0f, 0.05f, -1.0f),
            MaterialOverride = _matSent
        };
        _facingArrowRoot.AddChild(_facingArrowShaft);

        CylinderMesh headMesh = new()
        {
            TopRadius = 0.0f,
            BottomRadius = 0.45f,
            Height = 0.8f
        };
        _facingArrowHead = new MeshInstance3D
        {
            Mesh = headMesh,
            Rotation = new Vector3(-Mathf.Pi / 2f, 0f, 0f),
            Position = new Vector3(0f, 0.05f, -2.4f),
            MaterialOverride = _matSent
        };
        _facingArrowRoot.AddChild(_facingArrowHead);

        for (int i = 0; i < MaxGroupSlots; i++)
        {
            _markerPool[i] = new PooledMarker(
                this,
                ringMesh,
                dotMesh,
                ghostMesh,
                shaftMesh,
                headMesh,
                _matSent,
                _matGhost);

            _previewPool[i] = new PooledMarker(
                this,
                ringMesh,
                dotMesh,
                ghostMesh,
                shaftMesh,
                headMesh,
                _matSent,
                _matGhost);
        }

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
        _state = state;
        _timeInState = 0;
        _markerRoot.Visible = state != OrderMarkerVisualState.Hidden;
        ApplyMaterialForState(state);
        SetGroupState(state);
    }

    public StandardMaterial3D GetMaterialForState(OrderMarkerVisualState state) => state switch
    {
        OrderMarkerVisualState.Sent => _matSent,
        OrderMarkerVisualState.Processing => _matProcessing,
        OrderMarkerVisualState.PathFound => _matSuccess,
        OrderMarkerVisualState.Failed => _matFailed,
        OrderMarkerVisualState.Partial => _matPartial,
        _ => _matSent
    };

    private void ApplyMaterialForState(OrderMarkerVisualState state)
    {
        StandardMaterial3D mat = GetMaterialForState(state);
        _ring.MaterialOverride = mat;
        _centerDot.MaterialOverride = mat;
    }

    public void ShowGroupSlots(
        ReadOnlySpan<Vector3> targets,
        ReadOnlySpan<bool> isAdjusted,
        ReadOnlySpan<Vector3> originalTargets,
        float? facing,
        OrderMarkerVisualState state)
    {
        _state = state;
        _activeGroupSlotCount = Math.Min(targets.Length, MaxGroupSlots);
        StandardMaterial3D mat = GetMaterialForState(state);

        for (int i = 0; i < _activeGroupSlotCount; i++)
        {
            PooledMarker marker = _markerPool[i];
            marker.Root.Position = targets[i];
            marker.Root.Visible = true;
            marker.IsActive = true;
            marker.State = state;
            marker.TimeInState = 0;
            marker.ApplyMaterial(mat);

            if (isAdjusted.Length > i && isAdjusted[i] && originalTargets.Length > i)
            {
                marker.GhostRing.Position = originalTargets[i] - targets[i];
                marker.GhostRing.Visible = true;
            }
            else
            {
                marker.GhostRing.Visible = false;
            }

            if (facing.HasValue)
            {
                marker.FacingArrowRoot.Rotation = new Vector3(0f, facing.Value, 0f);
                marker.FacingArrowRoot.Visible = true;
            }
            else
            {
                marker.FacingArrowRoot.Visible = false;
            }
        }

        for (int i = _activeGroupSlotCount; i < MaxGroupSlots; i++)
        {
            _markerPool[i].Hide();
        }
    }

    public void SetGroupState(OrderMarkerVisualState state)
    {
        _state = state;
        StandardMaterial3D mat = GetMaterialForState(state);
        for (int i = 0; i < _activeGroupSlotCount; i++)
        {
            _markerPool[i].State = state;
            _markerPool[i].TimeInState = 0;
            _markerPool[i].ApplyMaterial(mat);
        }
    }

    public void ClearGroupSlots()
    {
        for (int i = 0; i < _activeGroupSlotCount; i++)
        {
            _markerPool[i].Hide();
        }
        _activeGroupSlotCount = 0;
    }

    public void ShowGroupPreviewSlots(
        ReadOnlySpan<Vector3> targets,
        ReadOnlySpan<bool> isAdjusted,
        ReadOnlySpan<Vector3> originalTargets,
        float? facing)
    {
        _activePreviewSlotCount = Math.Min(targets.Length, MaxGroupSlots);
        StandardMaterial3D mat = _matSent;

        for (int i = 0; i < _activePreviewSlotCount; i++)
        {
            PooledMarker marker = _previewPool[i];
            marker.Root.Position = targets[i];
            marker.Root.Visible = true;
            marker.IsActive = true;
            marker.State = OrderMarkerVisualState.Sent;
            marker.TimeInState = 0;
            marker.ApplyMaterial(mat);

            if (isAdjusted.Length > i && isAdjusted[i] && originalTargets.Length > i)
            {
                marker.GhostRing.Position = originalTargets[i] - targets[i];
                marker.GhostRing.Visible = true;
            }
            else
            {
                marker.GhostRing.Visible = false;
            }

            if (facing.HasValue)
            {
                marker.FacingArrowRoot.Rotation = new Vector3(0f, facing.Value, 0f);
                marker.FacingArrowRoot.Visible = true;
            }
            else
            {
                marker.FacingArrowRoot.Visible = false;
            }
        }

        for (int i = _activePreviewSlotCount; i < MaxGroupSlots; i++)
        {
            if (_previewPool[i].IsActive)
            {
                _previewPool[i].Hide();
            }
        }
    }

    public void ClearGroupPreviewSlots()
    {
        for (int i = 0; i < _activePreviewSlotCount; i++)
        {
            _previewPool[i].Hide();
        }
        _activePreviewSlotCount = 0;
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
        if (_activeGroupSlotCount > 0)
        {
            for (int i = 0; i < _activeGroupSlotCount; i++)
            {
                PooledMarker marker = _markerPool[i];
                if (!marker.IsActive) continue;

                marker.TimeInState += delta;
                switch (marker.State)
                {
                    case OrderMarkerVisualState.Sent:
                        {
                            float pulse = 1.0f + 0.08f * MathF.Sin((float)marker.TimeInState * 8.0f);
                            marker.Ring.Scale = new Vector3(pulse, 0.15f, pulse);
                            break;
                        }
                    case OrderMarkerVisualState.Processing:
                        {
                            float pulse = 1.0f + 0.14f * MathF.Sin((float)marker.TimeInState * 10.0f);
                            marker.Ring.Scale = new Vector3(pulse, 0.15f, pulse);
                            break;
                        }
                    case OrderMarkerVisualState.PathFound:
                        {
                            float pulse = 1.0f + 0.03f * MathF.Sin((float)marker.TimeInState * 3.0f);
                            marker.Ring.Scale = new Vector3(pulse, 0.15f, pulse);
                            if (marker.TimeInState > 3.0)
                            {
                                float fade = (float)Math.Clamp(1.0 - (marker.TimeInState - 3.0) / 1.5, 0.0, 1.0);
                                if (fade <= 0f)
                                {
                                    marker.Hide();
                                }
                            }
                            break;
                        }
                    case OrderMarkerVisualState.Failed:
                        {
                            marker.Ring.Scale = new Vector3(1.1f, 0.15f, 1.1f);
                            if (marker.TimeInState > 1.2)
                            {
                                float fade = (float)Math.Clamp(1.0 - (marker.TimeInState - 1.2) / 0.8, 0.0, 1.0);
                                if (fade <= 0f)
                                {
                                    marker.Hide();
                                }
                            }
                            break;
                        }
                    case OrderMarkerVisualState.Partial:
                        {
                            float pulse = 1.0f + 0.06f * MathF.Sin((float)marker.TimeInState * 5.0f);
                            marker.Ring.Scale = new Vector3(pulse, 0.15f, pulse);
                            if (marker.TimeInState > 4.0)
                            {
                                float fade = (float)Math.Clamp(1.0 - (marker.TimeInState - 4.0) / 1.5, 0.0, 1.0);
                                if (fade <= 0f)
                                {
                                    marker.Hide();
                                }
                            }
                            break;
                        }
                }

                if (marker.GhostRing.Visible)
                {
                    float ghostPulse = 1.0f + 0.05f * MathF.Sin((float)marker.TimeInState * 6.0f);
                    marker.GhostRing.Scale = new Vector3(ghostPulse, 0.15f, ghostPulse);
                }
            }
        }

        if (_activePreviewSlotCount > 0)
        {
            for (int i = 0; i < _activePreviewSlotCount; i++)
            {
                PooledMarker marker = _previewPool[i];
                if (!marker.IsActive) continue;

                marker.TimeInState += delta;
                float pulse = 1.0f + 0.08f * MathF.Sin((float)marker.TimeInState * 8.0f);
                marker.Ring.Scale = new Vector3(pulse, 0.15f, pulse);

                if (marker.GhostRing.Visible)
                {
                    float ghostPulse = 1.0f + 0.05f * MathF.Sin((float)marker.TimeInState * 6.0f);
                    marker.GhostRing.Scale = new Vector3(ghostPulse, 0.15f, ghostPulse);
                }
            }
        }

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

    public void ShowFacingArrow(Vector3 origin, float yaw)
    {
        _facingArrowRoot.Position = origin;
        _facingArrowRoot.Rotation = new Vector3(0f, yaw, 0f);
        _facingArrowRoot.Visible = true;
    }

    public void HideFacingArrow()
    {
        _facingArrowRoot.Visible = false;
    }

    public void ClearSubmittedMarker()
    {
        _markerRoot.Visible = false;
        _state = OrderMarkerVisualState.Hidden;
        _timeInState = 0;
        HideFacingArrow();
    }

    public void Clear()
    {
        ClearSubmittedMarker();
        HideGhost();
        HideFacingArrow();
        ClearGroupSlots();
        ClearGroupPreviewSlots();
    }
}
