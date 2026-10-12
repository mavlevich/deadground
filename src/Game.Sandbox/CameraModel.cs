using System.Numerics;

namespace DeadGround.Sandbox;

/// <summary>
/// Deterministic frame-rate independent camera calculation and state model.
/// Free of any Godot dependencies, allowing comprehensive headless and multi-rate unit testing.
/// </summary>
public sealed class CameraModel
{
    public const float DefaultBasePanSpeed = 120.0f;
    public const float DefaultReferenceSize = 112.0f;
    public const float DefaultZoomSharpness = 12.0f;
    public const float DefaultZoomStepFactor = 0.85f;
    public const float DefaultMinSize = 35.0f;
    public const float DefaultMaxSize = 500.0f;

    public Vector3 Focus { get; set; }
    public float CurrentSize { get; set; }
    public float TargetSize { get; set; }

    public float MinSize { get; set; } = DefaultMinSize;
    public float MaxSize { get; set; } = DefaultMaxSize;

    public float MinX { get; set; } = 0f;
    public float MaxX { get; set; } = 2000f;
    public float MinZ { get; set; } = 0f;
    public float MaxZ { get; set; } = 2000f;

    public float BasePanSpeed { get; set; } = DefaultBasePanSpeed;
    public float ReferenceSize { get; set; } = DefaultReferenceSize;
    public float ZoomSharpness { get; set; } = DefaultZoomSharpness;
    public float ZoomStepFactor { get; set; } = DefaultZoomStepFactor;

    private Vector2 _inputDirection = Vector2.Zero;

    public Vector2 InputDirection => _inputDirection;

    public CameraModel(Vector3 initialFocus, float initialSize)
    {
        Focus = initialFocus;
        CurrentSize = initialSize;
        TargetSize = initialSize;
    }

    public void SetBounds(float minX, float maxX, float minZ, float maxZ)
    {
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        ClampFocus();
    }

    public void SetSizeLimits(float minSize, float maxSize)
    {
        MinSize = minSize;
        MaxSize = maxSize;
        TargetSize = Math.Clamp(TargetSize, MinSize, MaxSize);
        CurrentSize = Math.Clamp(CurrentSize, MinSize, MaxSize);
    }

    public void SetInput(float moveX, float moveZ)
    {
        Vector2 dir = new(moveX, moveZ);
        float lenSq = dir.LengthSquared();
        if (lenSq > 1e-6f)
        {
            _inputDirection = dir / MathF.Sqrt(lenSq);
        }
        else
        {
            _inputDirection = Vector2.Zero;
        }
    }

    public void ClearInput()
    {
        _inputDirection = Vector2.Zero;
    }

    public void ZoomIn()
    {
        TargetSize = Math.Clamp(TargetSize * ZoomStepFactor, MinSize, MaxSize);
    }

    public void ZoomOut()
    {
        TargetSize = Math.Clamp(TargetSize / ZoomStepFactor, MinSize, MaxSize);
    }

    public void SetZoom(float targetSize)
    {
        TargetSize = Math.Clamp(targetSize, MinSize, MaxSize);
    }

    public void Reset(Vector3 defaultFocus, float defaultSize)
    {
        Focus = defaultFocus;
        CurrentSize = defaultSize;
        TargetSize = defaultSize;
        ClearInput();
        ClampFocus();
    }

    public void Update(float delta)
    {
        if (delta <= 0f) return;

        // Exact integration of zoom and pan over delta:
        // Size evolves continuously as: S(t) = TargetSize + (CurrentSize - TargetSize) * exp(-k * t)
        // Its integral over [0, delta] is: TargetSize * delta + (CurrentSize - TargetSize) * (1 - exp(-k * delta)) / k
        float startSize = CurrentSize;
        float diff = TargetSize - startSize;
        float integratedSize;
        float endSize;

        if (MathF.Abs(diff) > 1e-4f && ZoomSharpness > 1e-6f)
        {
            float expTerm = MathF.Exp(-ZoomSharpness * delta);
            float blend = 1f - expTerm;
            integratedSize = TargetSize * delta + (startSize - TargetSize) * blend / ZoomSharpness;
            endSize = startSize + diff * blend;
            if (MathF.Abs(TargetSize - endSize) < 1e-3f)
            {
                endSize = TargetSize;
            }
        }
        else
        {
            integratedSize = startSize * delta;
            endSize = TargetSize;
        }

        CurrentSize = endSize;

        // Move focus based on normalized input direction, integrated continuously over changing zoom
        if (_inputDirection != Vector2.Zero && ReferenceSize > 0f)
        {
            float averageSizeScale = integratedSize / (ReferenceSize * delta);
            float speed = BasePanSpeed * averageSizeScale;
            Vector2 displacement = _inputDirection * (speed * delta);
            Focus = new Vector3(Focus.X + displacement.X, Focus.Y, Focus.Z + displacement.Y);
            ClampFocus();
        }
    }

    private void ClampFocus()
    {
        float x = Math.Clamp(Focus.X, MinX, MaxX);
        float z = Math.Clamp(Focus.Z, MinZ, MaxZ);
        Focus = new Vector3(x, Focus.Y, z);
    }
}
