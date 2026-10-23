using DeadGround.Sandbox;
using Godot;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Connects the frame-rate independent CameraModel to Godot's Camera3D.
/// Eliminates double input processing and provides smooth zoom and panning.
/// </summary>
internal sealed class CameraRig
{
    private readonly Camera3D _camera;
    private readonly CameraModel _model;
    private readonly OrbitCameraPose _orbit = new();
    private Vector3 _defaultFocus;
    private float _defaultSize;

    public CameraModel Model => _model;
    public Vector3 Focus => new(_model.Focus.X, _model.Focus.Y, _model.Focus.Z);
    public float Size => _model.CurrentSize;
    public float ViewYaw => _orbit.Yaw;
    public float ViewPitch => _orbit.Pitch;
    public bool IsPerspective => _camera.Projection == Camera3D.ProjectionType.Perspective;
    public Func<float, float, float>? GroundHeight { get; set; }

    public void SetPerspective(bool enabled)
    {
        _camera.Projection = enabled ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal;
        _camera.Fov = 55f;
        SyncCamera();
    }

    public void DragOrbit(Vector2 relative)
    {
        _orbit.Drag(relative.X, relative.Y);
        SyncCamera();
    }

    public float FarClipMargin { get; set; } = 1000f;

    public void Orbit(float yawDelta, float pitchDelta)
    {
        _orbit.Rotate(yawDelta, pitchDelta);
        SyncCamera();
    }

    public bool HandleZoomInput(InputEvent input)
    {
        if (input is InputEventMagnifyGesture magnify)
        {
            ZoomByScale(magnify.Factor);
            return true;
        }
        if (input is InputEventMouseButton { Pressed: true } wheel &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float steps = wheel.Factor > 0f ? wheel.Factor : 1f;
            ZoomSteps(wheel.ButtonIndex == MouseButton.WheelUp ? steps : -steps);
            return true;
        }
        return false;
    }

    public CameraRig(Camera3D camera, Vector3 initialFocus, float initialSize, float mapWidth, float mapDepth)
    {
        _camera = camera;
        _defaultFocus = initialFocus;
        _defaultSize = initialSize;

        _model = new CameraModel(new NumericsVector3(initialFocus.X, initialFocus.Y, initialFocus.Z), initialSize);
        _model.SetBounds(0f, mapWidth, 0f, mapDepth);
        float maxSize = Mathf.Max(175f, mapWidth * 1.25f);
        _model.SetSizeLimits(35f, maxSize);

        SyncCamera();
    }

    public void SetDefaultView(Vector3 defaultFocus, float defaultSize)
    {
        _defaultFocus = defaultFocus;
        _defaultSize = defaultSize;
    }

    public void ResetToDefault()
    {
        _orbit.Reset();
        _model.Reset(new NumericsVector3(_defaultFocus.X, _defaultFocus.Y, _defaultFocus.Z), _defaultSize);
        SyncCamera();
    }

    public void CenterOn(Vector3 focus)
    {
        float x = Mathf.Clamp(focus.X, _model.MinX, _model.MaxX);
        float z = Mathf.Clamp(focus.Z, _model.MinZ, _model.MaxZ);
        _model.Focus = new NumericsVector3(x, focus.Y, z);
        SyncCamera();
    }

    public void ClearInput()
    {
        _model.ClearInput();
    }

    public void ZoomIn()
    {
        _model.ZoomIn();
    }

    public void ZoomOut()
    {
        _model.ZoomOut();
    }

    public void ZoomSteps(float steps) => _model.ZoomSteps(steps);

    public void ZoomByScale(float scale) => _model.ZoomByScale(scale);

    public void Update(double delta, bool allowInput = true)
    {
        // Clamp delta to prevent massive warps after resuming from background or long stalls
        float clampedDelta = (float)Mathf.Clamp(delta, 0.0, 0.1);

        if (allowInput)
        {
            float dx = 0f, dz = 0f;
            if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Left)) dx--;
            if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Right)) dx++;
            if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) || Input.IsKeyPressed(Key.Up)) dz--;
            if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) || Input.IsKeyPressed(Key.Down)) dz++;
            System.Numerics.Vector2 direction = _orbit.GroundInput(dx, dz);
            _model.SetInput(direction.X, direction.Y);
        }
        else
        {
            _model.ClearInput();
        }

        _model.Update(clampedDelta);
        SyncCamera();
    }

    internal static float CameraDistance(float size) => OrbitCameraPose.Distance(size, OrbitCameraPose.DefaultPitch);

    private void SyncCamera()
    {
        float size = _model.CurrentSize;
        float distance = IsPerspective
            ? MathF.Max(size / (2f * MathF.Tan(_camera.Fov * MathF.PI / 360f)), 12f / MathF.Sin(_orbit.Pitch))
            : OrbitCameraPose.Distance(size, _orbit.Pitch);
        _camera.Size = size;
        Vector3 focus = new(_model.Focus.X, _model.Focus.Y, _model.Focus.Z);
        System.Numerics.Vector3 offset = _orbit.OffsetDirection;
        Vector3 position = focus + new Vector3(offset.X, offset.Y, offset.Z) * distance;
        if (GroundHeight is not null)
            position.Y = MathF.Max(position.Y, GroundHeight(position.X, position.Z) + 6f);
        _camera.Position = position;
        _camera.Far = distance + size / MathF.Sin(_orbit.Pitch) + FarClipMargin;
        _camera.LookAt(focus, Vector3.Up);
    }
}
