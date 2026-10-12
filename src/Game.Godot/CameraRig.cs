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

    private void SyncCamera()
    {
        _camera.Size = _model.CurrentSize;
        _camera.Position = new Vector3(_model.Focus.X, _model.Focus.Y + 115f, _model.Focus.Z + 80f);
        _camera.LookAt(new Vector3(_model.Focus.X, _model.Focus.Y, _model.Focus.Z), Vector3.Up);
    }
}
