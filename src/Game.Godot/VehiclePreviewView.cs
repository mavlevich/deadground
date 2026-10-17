using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Standalone vehicle inspection and preview view for Dead Ground.
/// Renders the T-62 MBT alongside legacy prototype vehicles, ground grid,
/// human scale reference, camera presets, LOD inspection, and recoil/combat effects.
/// </summary>
public partial class VehiclePreviewView : Node3D
{
    public sealed record CameraPreset(
        string Name,
        Vector3 Position,
        Vector3 Target,
        bool Orthogonal = false,
        float OrthoSize = 10f);

    private readonly CameraPreset[] _presets =
    [
        new("Front", new Vector3(0, 2.8f, -16), new Vector3(0, 1.1f, -0.8f), true, 8.5f),
        new("Side Profile", new Vector3(16, 1.4f, -1.2f), new Vector3(0, 1.4f, -1.2f), true, 8.5f),
        new("Rear", new Vector3(0, 2.8f, 16), new Vector3(0, 1.1f, -0.8f), true, 8.5f),
        new("Top", new Vector3(0, 20, -1.2f), new Vector3(0, 0, -1.2f), true, 13.5f),
        new("Three-Quarter", new Vector3(10, 5, -13), new Vector3(0, 1, -0.8f), true, 8.5f),
        new("RTS Close", new Vector3(0, 31, 22), new Vector3(0, 0, 0), true, 38),
        new("RTS Normal", new Vector3(0, 61, 43), new Vector3(0, 0, 0), true, 75),
        new("RTS Distant", new Vector3(0, 131, 92), new Vector3(0, 0, 0), true, 160)
    ];

    private Camera3D _camera = null!;
    private Node3D _vehiclesContainer = null!;
    private Node3D _t62Root = null!;
    private Node3D _tankRoot = null!;
    private Node3D _reconRoot = null!;
    private Node3D? _t62ModelInstance;

    private Node3D? _turretYaw;
    private Node3D? _gunPitch;
    private Node3D? _barrelRecoil;
    private Node3D? _muzzle;
    private Vector3 _barrelBindPosition;

    private float _currentYaw;
    private float _currentPitch;
    private float _recoilAmount;

    private int _selectedLod = 0; // 0=LOD0, 1=LOD1, 2=LOD2, -1=Auto
    private int _effectiveLod = 0;
    private int _presetIndex = 4; // Default Three-Quarter Hero

    private Label3D _t62Label = null!;
    private Label _presetLabel = null!;
    private Label _lodLabel = null!;
    private Label _diagnosticsLabel = null!;

    private CombatEffects _combatEffects = null!;
    private MeshInstance3D _measuredGrid = null!;
    private Node3D _humanReference = null!;
    private bool _comparisonVisible;

    private string? _captureOutputDir;
    private int _captureIndex;
    private double _captureTimer;
    private readonly int[] _capturePresets = [4, 1, 0, 2, 3, 5, 6, 7];
    private readonly int[] _triangleCounts = new int[3];
    private bool _isCapturing;

    public Node3D? T62ModelInstance => _t62ModelInstance;
    public Node3D? TurretYawNode => _turretYaw;
    public Node3D? GunPitchNode => _gunPitch;
    public Node3D? BarrelRecoilNode => _barrelRecoil;
    public Node3D? MuzzleNode => _muzzle;
    public int EffectiveLod => _effectiveLod;
    public int SelectedLod => _selectedLod;
    internal Camera3D InspectionCamera => _camera;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        string[] allArgs = OS.GetCmdlineArgs();
        ParseCaptureArgs(userArgs, allArgs);

        using (System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(
            Godot.FileAccess.GetFileAsString("res://Assets/Vehicles/T62/manifest.json")))
        {
            foreach (System.Text.Json.JsonElement lod in manifest.RootElement.GetProperty("lods").EnumerateArray())
                _triangleCounts[lod.GetProperty("lod").GetInt32()] = lod.GetProperty("triangles").GetInt32();
        }
        SetupEnvironment();
        SetupGround();
        SetupHumanReference();

        _vehiclesContainer = new Node3D { Name = "VehiclesContainer" };
        AddChild(_vehiclesContainer);

        SetupCombatEffects();
        SetupVehicles();
        SetComparisonVisible(false);
        BuildHud();

        SelectPreset(_presetIndex);
        ApplyLod(_selectedLod);

        if (!string.IsNullOrEmpty(_captureOutputDir))
        {
            _isCapturing = true;
            _captureIndex = 0;
            _captureTimer = 0.25;
            SelectPreset(_capturePresets[0]);
        }
    }

    private void ParseCaptureArgs(string[] userArgs, string[] allArgs)
    {
        string[] args = userArgs.Length > 0 ? userArgs : allArgs;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vehicle-preview-variant" && i + 1 < args.Length)
            {
                _selectedLod = args[i + 1] switch
                {
                    "rts" => 1,
                    "distant" => 2,
                    _ => 0
                };
            }
            if (args[i] == "--vehicle-preview-capture" && i + 1 < args.Length)
            {
                _captureOutputDir = args[i + 1];
            }
        }
    }

    private void SetupEnvironment()
    {
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -35, 0),
            LightColor = new Color("e9e6de"),
            LightEnergy = 1.05f,
            ShadowEnabled = true
        });

        // Fill light to reveal shadowed running gear details
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(30, 145, 0),
            LightColor = new Color("c8d6e5"),
            LightEnergy = 0.25f,
            ShadowEnabled = false
        });

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("141d18"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("c5ccd0"),
                AmbientLightEnergy = 0.45f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                SsaoEnabled = true,
                SsaoRadius = 0.8f,
                SsaoIntensity = 0.7f
            }
        });

        _camera = new Camera3D
        {
            Far = 1000,
            Current = true,
            Fov = 45f
        };
        AddChild(_camera);
    }

    private void SetupGround()
    {
        // Dark neutral ground plane
        StandardMaterial3D groundMat = new()
        {
            AlbedoColor = new Color("51584f"),
            Roughness = 0.95f
        };

        MeshInstance3D ground = new()
        {
            Mesh = new PlaneMesh { Size = new Vector2(120, 120) },
            Position = Vector3.Zero,
            MaterialOverride = groundMat
        };
        AddChild(ground);

        // Measured 1-meter and 5-meter grid lines
        _measuredGrid = CreateMeasuredGrid(radiusMeters: 25);
        _measuredGrid.Visible = false;
        AddChild(_measuredGrid);
    }

    private static MeshInstance3D CreateMeasuredGrid(int radiusMeters)
    {
        List<Vector3> minorLines = [];
        List<Vector3> majorLines = [];

        for (int i = -radiusMeters; i <= radiusMeters; i++)
        {
            float coord = i;
            bool isMajor = (i % 5 == 0);
            List<Vector3> targetList = isMajor ? majorLines : minorLines;

            // X-parallel lines
            targetList.Add(new Vector3(-radiusMeters, 0.005f, coord));
            targetList.Add(new Vector3(radiusMeters, 0.005f, coord));

            // Z-parallel lines
            targetList.Add(new Vector3(coord, 0.005f, -radiusMeters));
            targetList.Add(new Vector3(coord, 0.005f, radiusMeters));
        }

        ArrayMesh mesh = new();

        if (minorLines.Count > 0)
        {
            using Godot.Collections.Array minorArrays = new();
            minorArrays.Resize((int)Mesh.ArrayType.Max);
            minorArrays[(int)Mesh.ArrayType.Vertex] = minorLines.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, minorArrays);
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                AlbedoColor = new Color(0.28f, 0.35f, 0.30f, 0.50f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            });
        }

        if (majorLines.Count > 0)
        {
            using Godot.Collections.Array majorArrays = new();
            majorArrays.Resize((int)Mesh.ArrayType.Max);
            majorArrays[(int)Mesh.ArrayType.Vertex] = majorLines.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, majorArrays);
            int surfIdx = mesh.GetSurfaceCount() - 1;
            mesh.SurfaceSetMaterial(surfIdx, new StandardMaterial3D
            {
                AlbedoColor = new Color(0.48f, 0.58f, 0.45f, 0.90f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            });
        }

        return new MeshInstance3D
        {
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }

    private void SetupHumanReference()
    {
        Node3D humanRoot = new() { Position = new Vector3(-2.4f, 0, 0) };
        _humanReference = humanRoot;
        AddChild(humanRoot);

        StandardMaterial3D humanMat = new()
        {
            AlbedoColor = new Color("8e9982"),
            Roughness = 0.85f
        };

        // Torso / body: height 1.40 m (center Y = 0.70)
        MeshInstance3D body = new()
        {
            Mesh = new BoxMesh { Size = new Vector3(0.40f, 1.40f, 0.25f) },
            Position = new Vector3(0, 0.70f, 0),
            MaterialOverride = humanMat
        };
        humanRoot.AddChild(body);

        // Head: diameter 0.24 m, height reaches exactly 1.80 m (summit Y = 1.80)
        MeshInstance3D head = new()
        {
            Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f },
            Position = new Vector3(0, 1.68f, 0),
            MaterialOverride = humanMat
        };
        humanRoot.AddChild(head);

        Label3D humanLabel = new()
        {
            Text = "Human Reference\n1.80 m Height",
            Position = new Vector3(0, 2.15f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 18,
            Modulate = new Color("edc76b")
        };
        humanRoot.AddChild(humanLabel);
    }

    private void SetupCombatEffects()
    {
        _combatEffects = new CombatEffects(HeightMap.CreatePrototype());
        AddChild(_combatEffects);
    }

    private void SetupVehicles()
    {
        // 1. Center Station: T-62 Main Battle Tank (True physical scale 1.0)
        _t62Root = new Node3D { Name = "T62_Station", Position = new Vector3(0, 0, 0) };
        _vehiclesContainer.AddChild(_t62Root);

        _t62Label = new Label3D
        {
            Position = new Vector3(0, 3.2f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 20,
            Modulate = new Color("ffffff")
        };
        _t62Root.AddChild(_t62Label);

        // 2. Middle-Left Station: Recon Vehicle (Legacy game scale 0.75)
        _reconRoot = new Node3D { Name = "ReconVehicle_Station", Position = new Vector3(-4.8f, 0, 0) };
        _vehiclesContainer.AddChild(_reconRoot);

        PackedScene? reconScene = GD.Load<PackedScene>("res://Assets/Vehicles/recon.glb");
        if (reconScene is not null)
        {
            Node3D recon = reconScene.Instantiate<Node3D>();
            recon.Scale = Vector3.One * 0.75f;
            _reconRoot.AddChild(recon);
        }

        Label3D reconLabel = new()
        {
            Text = "RECON / legacy scale",
            Position = new Vector3(0, 2.5f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 18,
            Modulate = new Color("b0bcad")
        };
        _reconRoot.AddChild(reconLabel);

        // 3. Far-Left Station: Prototype Tank (Legacy game scale 0.75)
        _tankRoot = new Node3D { Name = "PrototypeTank_Station", Position = new Vector3(-8.2f, 0, 0) };
        _vehiclesContainer.AddChild(_tankRoot);

        PackedScene? tankScene = GD.Load<PackedScene>("res://Assets/Vehicles/tank.glb");
        if (tankScene is not null)
        {
            Node3D tank = tankScene.Instantiate<Node3D>();
            tank.Scale = Vector3.One * 0.75f;
            _tankRoot.AddChild(tank);
        }

        Label3D tankLabel = new()
        {
            Text = "PROTOTYPE / legacy scale",
            Position = new Vector3(0, 2.9f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 18,
            Modulate = new Color("b0bcad")
        };
        _tankRoot.AddChild(tankLabel);
    }

    public void ApplyLod(int lodIndex)
    {
        _selectedLod = lodIndex;
        int targetLod = lodIndex == -1 ? DetermineAutoLod() : lodIndex;
        LoadT62Model(targetLod);
    }

    private int DetermineAutoLod()
    {
        if (_camera is null) return 0;
        float distance = _camera.Projection == Camera3D.ProjectionType.Orthogonal ? _camera.Size : _camera.Position.DistanceTo(Vector3.Zero);
        if (distance < UnitView.Lod1CameraSize) return 0;
        if (distance < UnitView.Lod2CameraSize) return 1;
        return 2;
    }

    private void LoadT62Model(int lod)
    {
        _effectiveLod = lod;

        if (_t62ModelInstance is not null)
        {
            _t62Root.RemoveChild(_t62ModelInstance);
            _t62ModelInstance.QueueFree();
            _t62ModelInstance = null;
        }

        string scenePath = lod switch
        {
            1 => "res://Assets/Vehicles/T62/t62_lod1.glb",
            2 => "res://Assets/Vehicles/T62/t62_lod2.glb",
            _ => "res://Assets/Vehicles/T62/t62.glb"
        };

        PackedScene? scene = GD.Load<PackedScene>(scenePath);
        if (scene is null)
        {
            GD.PushError($"Failed to load T-62 model from {scenePath}");
            return;
        }

        _t62ModelInstance = scene.Instantiate<Node3D>();
        _t62ModelInstance.Name = $"T62_LOD{lod}";
        _t62ModelInstance.Scale = Vector3.One; // True scale 1.0
        _t62Root.AddChild(_t62ModelInstance);

        // Find functional hierarchy nodes
        _turretYaw = _t62ModelInstance.FindChild("TurretYaw", true, false) as Node3D;
        _gunPitch = _t62ModelInstance.FindChild("GunPitch", true, false) as Node3D;
        _barrelRecoil = _t62ModelInstance.FindChild("BarrelRecoil", true, false) as Node3D;
        _muzzle = _t62ModelInstance.FindChild("Muzzle", true, false) as Node3D;

        _barrelBindPosition = _barrelRecoil?.Position ?? Vector3.Zero;
        if (_barrelRecoil is not null)
            _barrelRecoil.Position = _barrelBindPosition + new Vector3(0, 0, _recoilAmount);

        // Reapply current articulation angles
        SetTurretYaw(_currentYaw);
        SetGunPitch(_currentPitch);

        string role = lod switch { 1 => "RTS", 2 => "DISTANT", _ => "CLOSE" };
        string lodName = $"{role} / LOD{lod} ({_triangleCounts[lod]:N0} tris)";

        string autoTag = _selectedLod == -1 ? " [Auto]" : string.Empty;
        _t62Label.Text = $"T-62 / LOD{lod}{autoTag}";

        if (_lodLabel is not null)
        {
            _lodLabel.Text = $"ACTIVE MODEL: {lodName}{autoTag}";
        }
    }

    public void SetTurretYaw(float yawDegrees)
    {
        _currentYaw = yawDegrees;
        if (_turretYaw is not null)
        {
            _turretYaw.RotationDegrees = new Vector3(0, yawDegrees, 0);
        }
        UpdateDiagnostics();
    }

    public void SetGunPitch(float pitchDegrees)
    {
        // Documented T-62 elevation limits: -6 deg depression to +16 deg elevation
        _currentPitch = Mathf.Clamp(pitchDegrees, -6f, 16f);
        if (_gunPitch is not null)
        {
            _gunPitch.RotationDegrees = new Vector3(_currentPitch, 0, 0);
        }
        UpdateDiagnostics();
    }

    public void ResetPose()
    {
        SetTurretYaw(0f);
        SetGunPitch(0f);
        _recoilAmount = 0f;
        if (_barrelRecoil is not null)
        {
            _barrelRecoil.Position = _barrelBindPosition;
        }
    }

    public void FireTestShot()
    {
        Vector3 origin = new(0, 1.85f, -6.02f);
        Vector3 shotDir = Vector3.Forward;

        if (_muzzle is not null)
        {
            origin = _muzzle.GlobalPosition;
            shotDir = -_muzzle.GlobalBasis.Z.Normalized();
        }

        Vector3 target = origin + shotDir * 50.0f;

        // Visual and audio combat feedback
        _combatEffects.SpawnDirectShot(origin, target);

        // Apply physical recoil to BarrelRecoil node
        _recoilAmount = 0.35f;
        if (_barrelRecoil is not null)
        {
            _barrelRecoil.Position = _barrelBindPosition + new Vector3(0, 0, _recoilAmount);
        }
    }

    public void SelectPreset(int index)
    {
        _presetIndex = Mathf.Wrap(index, 0, _presets.Length);
        CameraPreset preset = _presets[_presetIndex];

        _camera.Projection = preset.Orthogonal
            ? Camera3D.ProjectionType.Orthogonal
            : Camera3D.ProjectionType.Perspective;

        if (preset.Orthogonal)
        {
            _camera.Size = preset.OrthoSize;
        }

        _camera.Position = preset.Position;
        Vector3 lookDirection = preset.Target - preset.Position;
        Vector3 up = MathF.Abs(lookDirection.Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        _camera.LookAt(preset.Target, up);
        SetInspectionLabelsVisible(preset.OrthoSize >= 38);

        if (_presetLabel is not null)
        {
            _presetLabel.Text = $"CAMERA PRESET [{_presetIndex + 1}/{_presets.Length}]: {preset.Name.ToUpperInvariant()}";
        }

        if (_selectedLod == -1)
        {
            LoadT62Model(DetermineAutoLod());
        }

        UpdateDiagnostics();
    }

    private void SetComparisonVisible(bool visible)
    {
        _comparisonVisible = visible;
        _tankRoot.Visible = visible;
        _reconRoot.Visible = visible;
        _humanReference.Visible = visible;
    }

    private void SetInspectionLabelsVisible(bool visible)
    {
        foreach (Node station in _vehiclesContainer.GetChildren())
            foreach (Node child in station.GetChildren())
                if (child is Label3D label) label.Visible = visible;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Smooth recoil recovery
        if (_recoilAmount > 0f)
        {
            _recoilAmount = Mathf.MoveToward(_recoilAmount, 0f, dt * 3.5f);
            if (_barrelRecoil is not null)
            {
                _barrelRecoil.Position = _barrelBindPosition + new Vector3(0, 0, _recoilAmount);
            }
        }

        // Advance visual combat effects
        _combatEffects.Update(delta);
        _combatEffects.UpdateListener(_camera.Position);

        // Automated capture runner
        if (_isCapturing)
        {
            _captureTimer -= delta;
            if (_captureTimer <= 0)
            {
                CaptureCurrentFrame();
                _captureIndex++;
                if (_captureIndex < _capturePresets.Length)
                {
                    SelectPreset(_capturePresets[_captureIndex]);
                    _captureTimer = 0.20;
                }
                else
                {
                    _isCapturing = false;
                    GD.Print($"VEHICLE_PREVIEW_CAPTURES_OK {_capturePresets.Length} frames saved to {_captureOutputDir}");
                    GetTree().Quit(0);
                }
            }
        }
    }

    private void CaptureCurrentFrame()
    {
        if (string.IsNullOrEmpty(_captureOutputDir)) return;

        DirAccess.MakeDirAbsolute(_captureOutputDir);
        Image? img = GetViewport().GetTexture().GetImage();
        if (img is not null)
        {
            string presetName = _presets[_presetIndex].Name.ToLowerInvariant().Replace(" ", "_").Replace("(", "").Replace(")", "").Replace("/", "_");
            string filename = $"t62_preview_{_captureIndex}_{presetName}.png";
            string fullPath = $"{_captureOutputDir}/{filename}";
            Error err = img.SavePng(fullPath);
            if (err == Error.Ok)
            {
                GD.Print($"SAVED_PREVIEW_FRAME {fullPath}");
            }
            else
            {
                GD.PushError($"Failed to save preview frame to {fullPath}: {err}");
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
            return;

        switch (keyEvent.Keycode)
        {
            case Key.A or Key.Left:
                SetTurretYaw(_currentYaw - 15f);
                break;
            case Key.D or Key.Right:
                SetTurretYaw(_currentYaw + 15f);
                break;
            case Key.W or Key.Up:
                SetGunPitch(_currentPitch + 2f);
                break;
            case Key.S or Key.Down:
                SetGunPitch(_currentPitch - 2f);
                break;
            case Key.Space:
                FireTestShot();
                break;
            case Key.R:
                ResetPose();
                break;
            case Key.Tab:
                SetComparisonVisible(!_comparisonVisible);
                break;
            case Key.G:
                _measuredGrid.Visible = !_measuredGrid.Visible;
                break;
            case Key.C:
                SelectPreset(_presetIndex + 1);
                break;
            case Key.Key1:
                ApplyLod(0);
                break;
            case Key.Key2:
                ApplyLod(1);
                break;
            case Key.Key3:
                ApplyLod(2);
                break;
            case Key.Key4 or Key.Key0:
                ApplyLod(-1); // Auto LOD
                break;
        }
    }

    private void BuildHud()
    {
        CanvasLayer canvas = new();
        AddChild(canvas);

        // Top info header
        PanelContainer topPanel = new()
        {
            Position = new Vector2(24, 20),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        topPanel.AddThemeStyleboxOverride("panel", PanelStyle());

        MarginContainer topMargin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            topMargin.AddThemeConstantOverride("margin_" + side, 16);
        }

        VBoxContainer topStack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        topStack.AddThemeConstantOverride("separation", 6);

        Label title = new()
        {
            Text = "DEAD GROUND   /   T-62 VEHICLE ASSET PREVIEW",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", new Color("edc76b"));
        topStack.AddChild(title);

        _presetLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _presetLabel.AddThemeFontSizeOverride("font_size", 14);
        _presetLabel.AddThemeColorOverride("font_color", new Color("ffffff"));
        topStack.AddChild(_presetLabel);

        _lodLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _lodLabel.AddThemeFontSizeOverride("font_size", 13);
        _lodLabel.AddThemeColorOverride("font_color", new Color("b0bcad"));
        topStack.AddChild(_lodLabel);

        _diagnosticsLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _diagnosticsLabel.AddThemeFontSizeOverride("font_size", 12);
        _diagnosticsLabel.AddThemeColorOverride("font_color", new Color("788574"));
        topStack.AddChild(_diagnosticsLabel);

        topMargin.AddChild(topStack);
        topPanel.AddChild(topMargin);
        canvas.AddChild(topPanel);

        // Bottom controls footer
        PanelContainer bottomPanel = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.AddChild(bottomPanel);
        bottomPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottomPanel.OffsetLeft = 24;
        bottomPanel.OffsetRight = -24;
        bottomPanel.OffsetTop = -82;
        bottomPanel.OffsetBottom = -18;
        bottomPanel.AddThemeStyleboxOverride("panel", PanelStyle());

        MarginContainer bottomMargin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            bottomMargin.AddThemeConstantOverride("margin_" + side, 10);
        }

        Label controls = new()
        {
            Text = "[C] Camera / [A/D] Turret / [W/S] Gun / [Space] Fire / [R] Reset\n[1/2/3/4] LOD 0/1/2/Auto / [Tab] Comparison and scale reference / [G] Measured grid",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controls.AddThemeFontSizeOverride("font_size", 13);
        controls.AddThemeColorOverride("font_color", new Color("edc76b"));

        bottomMargin.AddChild(controls);
        bottomPanel.AddChild(bottomMargin);

        UpdateDiagnostics();
    }

    private void UpdateDiagnostics()
    {
        if (_diagnosticsLabel is null) return;
        _diagnosticsLabel.Text = $"Turret Yaw: {_currentYaw:F1} deg  |  Gun Pitch: {_currentPitch:F1} deg (-6 to +16)  |  Muzzle: ({_muzzle?.GlobalPosition.X ?? 0:F2}, {_muzzle?.GlobalPosition.Y ?? 0:F2}, {_muzzle?.GlobalPosition.Z ?? 0:F2})";
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.06f, 0.08f, 0.07f, 0.92f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 5,
        CornerRadiusBottomRight = 5,
        CornerRadiusTopLeft = 5,
        CornerRadiusTopRight = 5
    };
}
