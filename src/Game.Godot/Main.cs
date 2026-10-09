using Godot;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

public partial class Main : Node3D
{
    private HeightMap _map = null!;
    private LocalSession _session = null!;
    private Camera3D _camera = null!;
    private Label _stats = null!, _status = null!;
    private UnitViewSet _unitViews = null!;
    private MeshInstance3D _destination = null!;
    private Vector3 _focus = new(64, 0, 64);
    private double _hudTimer, _smokeTimer;
    private bool _smokeTest;
    private bool _terrainWindingValid;
    private long _lastBytes;
    private uint _lastCommandResult;

    public override void _Ready()
    {
        _smokeTest = OS.GetCmdlineUserArgs().Contains("--smoke-test");
        if (_smokeTest && !ClientRegressionTests.Run(this))
        {
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }
        _map = HeightMap.CreatePrototype();
        _session = new LocalSession(PrototypeScenario.Create(_map));
        MeshInstance3D terrain = TerrainView.Create(_map);
        AddChild(terrain);
        _terrainWindingValid = !_smokeTest || TerrainView.HasUpwardFrontFaces(terrain);
        AddChild(TerrainView.CreateGrid(_map));
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -35, 0), LightColor = new Color("fff0d2"),
            LightEnergy = 1.2f, ShadowEnabled = true
        });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("17211c"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("b9c6b2"), AmbientLightEnergy = 0.55f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic
            }
        });
        _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 112, Far = 1200, Current = true };
        AddChild(_camera);
        UpdateCamera();
        _unitViews = new UnitViewSet(this, _map);
        foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
        {
            // Demo movement is sent as normal client commands through the byte transport.
            _session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
        }
        if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
        _destination = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 1.5f, OuterRadius = 1.8f, Rings = 24, RingSegments = 8 },
            Scale = new Vector3(1, 0.15f, 1),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("edc76b"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            Visible = false
        };
        AddChild(_destination);
        BuildHud();
        UpdateViews();
        GD.Print($"M0_CLIENT_READY runtime={System.Environment.Version} own_units={_session.Replica.OwnUnits.Length}");
    }

    public override void _Process(double delta)
    {
        _session.Advance(delta);
        UpdateViews();
        if (_session.LastCommandResult is { } result && result.Sequence != _lastCommandResult)
        {
            _lastCommandResult = result.Sequence;
            _status.Text = result.Queued ? "Move order queued" : "Move order rejected / select a unit and try again";
            if (!result.Queued) _destination.Visible = false;
        }
        float dx = 0, dz = 0;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) dx--;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) dx++;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) dz--;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) dz++;
        if (dx != 0 || dz != 0)
        {
            _focus += new Vector3(dx, 0, dz).Normalized() * (float)delta * _camera.Size * 0.6f;
            _focus.X = Mathf.Clamp(_focus.X, 0, _map.Width);
            _focus.Z = Mathf.Clamp(_focus.Z, 0, _map.Depth);
            UpdateCamera();
        }
        _hudTimer += delta;
        if (_hudTimer >= 0.5)
        {
            double rate = (_session.BytesReceived - _lastBytes) / _hudTimer / 1024;
            _lastBytes = _session.BytesReceived;
            _stats.Text = $"20 Hz SIM  /  10 Hz STATE  /  TICK {_session.Tick:00000}  /  {_session.Replica.OwnUnits.Length} UNITS\n{_session.LastTickMilliseconds:F3} ms per tick  /  {_session.LastTickAllocatedBytes} B tick allocations  /  {rate:F1} KiB/s";
            _hudTimer = 0;
        }
        if (_smokeTest)
        {
            _smokeTimer += delta;
            if (_smokeTimer >= 1)
            {
                bool ok = _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 12 &&
                    _session.Replica.OwnUnits[0].Position.Y > 24;
                GD.Print(ok ? "M0_CLIENT_SMOKE_OK" : "M0_CLIENT_SMOKE_FAILED");
                GetTree().Quit(ok ? 0 : 1);
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.Space) { _focus = new Vector3(64, 0, 64); _camera.Size = 112; UpdateCamera(); }
            if (key.Keycode == Key.Escape) { _unitViews.Selected.Clear(); _status.Text = "No units selected"; }
        }
        if (@event is not InputEventMouseButton { Pressed: true } mouse) return;
        if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _camera.Size = Mathf.Clamp(_camera.Size * (mouse.ButtonIndex == MouseButton.WheelUp ? 0.9f : 1.1f), 35, 175);
            return;
        }
        if (mouse.ButtonIndex == MouseButton.Left)
        {
            EntityId? nearest = null;
            float nearestDistance = 24 * 24;
            foreach ((EntityId id, UnitView view) in _unitViews.Views)
            {
                Vector3 center = view.Root.Position + Vector3.Up;
                if (_camera.IsPositionBehind(center)) continue;
                float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mouse.Position);
                if (distance < nearestDistance) { nearestDistance = distance; nearest = id; }
            }
            if (!mouse.ShiftPressed) _unitViews.Selected.Clear();
            if (nearest is { } selected) _unitViews.Selected.Add(selected);
            _status.Text = _unitViews.Selected.Count == 0 ? "No units selected" : $"{_unitViews.Selected.Count} selected / right-click terrain to move";
        }
        if (mouse.ButtonIndex == MouseButton.Right && _unitViews.Selected.Count > 0)
        {
            Vector3 origin = _camera.ProjectRayOrigin(mouse.Position);
            Vector3 direction = _camera.ProjectRayNormal(mouse.Position);
            if (!_map.TryRaycast(new NumericsVector3(origin.X, origin.Y, origin.Z),
                new NumericsVector3(direction.X, direction.Y, direction.Z), _camera.Far, out NumericsVector3 hit)) return;
            int sent = 0;
            foreach (EntityId id in _unitViews.Selected)
                if (_session.Move(id, new NumericsVector2(hit.X, hit.Z))) sent++;
            _destination.Position = new Vector3(hit.X, hit.Y + 0.12f, hit.Z);
            _destination.Visible = sent > 0;
            _status.Text = $"Move order sent / {sent} units / {hit.X:F0}, {hit.Z:F0} m";
        }
    }

    private void UpdateViews()
    {
        int selected = _unitViews.Selected.Count;
        _unitViews.Sync(_session.Replica, _session.RenderTick);
        if (selected != _unitViews.Selected.Count)
            _status.Text = $"{_unitViews.Selected.Count} selected / unit removed from view";
    }

    private void UpdateCamera()
    {
        _camera.Position = _focus + new Vector3(0, 115, 80);
        _camera.LookAt(_focus, Vector3.Up);
    }

    private void BuildHud()
    {
        CanvasLayer canvas = new();
        AddChild(canvas);
        PanelContainer top = new() { Position = new Vector2(24, 24), MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AddThemeStyleboxOverride("panel", PanelStyle());
        MarginContainer margin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
        VBoxContainer stack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 9);
        Label title = new() { Text = "DEAD GROUND   /   M0 FIELD TEST", MouseFilter = Control.MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 21);
        title.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(title);
        Label subtitle = new() { Text = "Local authoritative simulation / prototype terrain", MouseFilter = Control.MouseFilterEnum.Ignore };
        subtitle.AddThemeColorOverride("font_color", new Color("b2beb1"));
        stack.AddChild(subtitle);
        _stats = new Label { Text = "Starting simulation...", MouseFilter = Control.MouseFilterEnum.Ignore };
        _stats.AddThemeFontSizeOverride("font_size", 14);
        stack.AddChild(_stats);
        margin.AddChild(stack); top.AddChild(margin); canvas.AddChild(top);

        PanelContainer bottom = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.AddChild(bottom);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.OffsetLeft = 24; bottom.OffsetRight = -24; bottom.OffsetTop = -88; bottom.OffsetBottom = -24;
        bottom.AddThemeStyleboxOverride("panel", PanelStyle());
        VBoxContainer instructions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        Label controls = new()
        {
            Text = "  LMB select   /   Shift + LMB add   /   RMB move   /   WASD pan   /   Wheel zoom   /   Space overview",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controls.AddThemeFontSizeOverride("font_size", 15);
        _status = new Label { Text = "  Demo movement active / one unit selected", MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeColorOverride("font_color", new Color("edc76b"));
        instructions.AddChild(controls); instructions.AddChild(_status); bottom.AddChild(instructions);
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.055f, 0.08f, 0.065f, 0.94f),
        BorderColor = new Color("46533e"), BorderWidthBottom = 1, BorderWidthTop = 1,
        BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6
    };

    public override void _ExitTree() => _session?.Dispose();
}
