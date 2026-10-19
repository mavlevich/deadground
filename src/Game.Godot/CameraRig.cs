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
    private Vector3 _defaultFocus;
    private float _defaultSize;

    public CameraModel Model => _model;
    public Vector3 Focus => new(_model.Focus.X, _model.Focus.Y, _model.Focus.Z);
    public float Size => _model.CurrentSize;

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
            _model.SetInput(dx, dz);
        }
        else
        {
            _model.ClearInput();
        }

        _model.Update(clampedDelta);
        SyncCamera();
    }

    // The camera looks down at the focus along a fixed direction (up 115, back 80 at the closest
    // zoom). An orthographic view of height Size starts its bottom rows Size/2 * cot(pitch) closer to
    // the ground than the focus, so the camera backs off with the zoom; otherwise the near half of a
    // wide view falls behind the camera and is clipped.
    internal const float BaseDistance = 140f;
    private static readonly Vector3 ViewOffsetDirection = new Vector3(0f, 115f, 80f).Normalized();
    private const float TerrainMargin = 150f;

    internal static float CameraDistance(float size)
    {
        float cotPitch = ViewOffsetDirection.Z / ViewOffsetDirection.Y;
        return Mathf.Max(BaseDistance, size * 0.5f * cotPitch + TerrainMargin);
    }

    private void SyncCamera()
    {
        float size = _model.CurrentSize;
        float distance = CameraDistance(size);
        _camera.Size = size;
        Vector3 focus = new(_model.Focus.X, _model.Focus.Y, _model.Focus.Z);
        _camera.Position = focus + ViewOffsetDirection * distance;
        _camera.Far = distance + size + 1000f;
        _camera.LookAt(focus, Vector3.Up);
    }
}
