using DeadGround.Engine;
using DeadGround.Sandbox;
using Godot;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Standalone visual sandbox for inspecting 3D line-of-sight queries,
/// terrain occlusion, structure obstacles, and hull-down visibility presets.
/// </summary>
public partial class LosSandboxView : Node3D
{
    private StaticGeometry _geometry = null!;
    private IReadOnlyList<LosSandboxPreset> _presets = null!;
    private int _presetIndex;

    private Camera3D _camera = null!;
    private Vector3 _cameraFocus;
    private float _cameraSize;

    private Node3D _scenarioContainer = null!;
    private Label _presetTitle = null!;
    private Label _presetDescription = null!;
    private Label _diagnosticsLabel = null!;

    public override void _Ready()
    {
        _geometry = LosSandboxScenario.CreateGeometry();
        _presets = LosSandboxScenario.CreatePresets();

        SetupEnvironment();
        SetupTerrain();
        SetupStructures();

        _scenarioContainer = new Node3D();
        AddChild(_scenarioContainer);

        BuildHud();
        SelectPreset(0);
    }

    private void SetupEnvironment()
    {
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -35, 0),
            LightColor = new Color("fff0d2"),
            LightEnergy = 1.2f,
            ShadowEnabled = true
        });

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("141d18"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("b9c6b2"),
                AmbientLightEnergy = 0.55f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic
            }
        });

        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Far = 1200,
            Current = true
        };
        AddChild(_camera);
    }

    private void SetupTerrain()
    {
        MeshInstance3D terrain = TerrainView.Create(_geometry.HeightMap);
        AddChild(terrain);
        AddChild(TerrainView.CreateGrid(_geometry.HeightMap));
    }

    private void SetupStructures()
    {
        StandardMaterial3D buildingMat = new()
        {
            AlbedoColor = new Color("5c5346"),
            Roughness = 0.85f
        };

        StandardMaterial3D wallMat = new()
        {
            AlbedoColor = new Color("6b756c"),
            Roughness = 0.9f
        };

        foreach (OpaqueStructure structure in _geometry.Structures)
        {
            float sizeX = structure.Max.X - structure.Min.X;
            float sizeY = structure.Max.Y - structure.Min.Y;
            float sizeZ = structure.Max.Z - structure.Min.Z;

            Vector3 center = new(
                (structure.Min.X + structure.Max.X) * 0.5f,
                (structure.Min.Y + structure.Max.Y) * 0.5f,
                (structure.Min.Z + structure.Max.Z) * 0.5f);

            MeshInstance3D meshInstance = new()
            {
                Mesh = new BoxMesh { Size = new Vector3(sizeX, sizeY, sizeZ) },
                Position = center,
                MaterialOverride = structure.Id == LosSandboxScenario.BuildingId ? buildingMat : wallMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On
            };
            AddChild(meshInstance);
        }
    }

    public override void _Process(double delta)
    {
        float dx = 0, dz = 0;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) dx--;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) dx++;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) dz--;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) dz++;

        if (dx != 0 || dz != 0)
        {
            _cameraFocus += new Vector3(dx, 0, dz).Normalized() * (float)delta * _camera.Size * 0.6f;
            _cameraFocus.X = Mathf.Clamp(_cameraFocus.X, 0, _geometry.HeightMap.Width);
            _cameraFocus.Z = Mathf.Clamp(_cameraFocus.Z, 0, _geometry.HeightMap.Depth);
            UpdateCamera();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.Key1: SelectPreset(0); return;
                case Key.Key2: SelectPreset(1); return;
                case Key.Key3: SelectPreset(2); return;
                case Key.Key4: SelectPreset(3); return;
                case Key.Key5: SelectPreset(4); return;
                case Key.Key6: SelectPreset(5); return;
                case Key.Space:
                    ResetCameraToPreset();
                    return;
                case Key.Bracketleft:
                    SelectPreset((_presetIndex - 1 + _presets.Count) % _presets.Count);
                    return;
                case Key.Bracketright:
                    SelectPreset((_presetIndex + 1) % _presets.Count);
                    return;
            }
        }

        if (@event is InputEventMouseButton { Pressed: true } mouse)
        {
            if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _cameraSize = Mathf.Clamp(
                    _cameraSize * (mouse.ButtonIndex == MouseButton.WheelUp ? 0.9f : 1.1f),
                    18f,
                    150f);
                _camera.Size = _cameraSize;
            }
        }
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        _presetIndex = index;
        LosSandboxPreset preset = _presets[index];

        _cameraFocus = ToGodot(preset.CameraFocus);
        _cameraSize = preset.CameraSize;
        ResetCameraToPreset();

        RebuildScenarioVisuals(preset);
        UpdateHud(preset);
    }

    private void ResetCameraToPreset()
    {
        _camera.Size = _cameraSize;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        _camera.Position = _cameraFocus + new Vector3(0, 115, 80);
        _camera.LookAt(_cameraFocus, Vector3.Up);
    }

    private void RebuildScenarioVisuals(LosSandboxPreset preset)
    {
        foreach (Node child in _scenarioContainer.GetChildren())
        {
            child.QueueFree();
        }

        Vector3 observerPos = ToGodot(preset.Observer);
        float observerGroundY = _geometry.HeightMap.GetHeight(observerPos.X, observerPos.Z);

        _scenarioContainer.AddChild(CreateMarker(
            observerPos,
            new Color("3da8ff"),
            radius: 0.5f,
            height: 1.0f));

        _scenarioContainer.AddChild(CreateLine(
            new Vector3(observerPos.X, observerGroundY, observerPos.Z),
            observerPos,
            new Color("3da8ff", 0.5f)));

        for (int i = 0; i < preset.TargetPoints.Length; i++)
        {
            NumericsVector3 targetNumerics = preset.TargetPoints[i];
            Vector3 targetPos = ToGodot(targetNumerics);
            float targetGroundY = _geometry.HeightMap.GetHeight(targetPos.X, targetPos.Z);

            _scenarioContainer.AddChild(CreateMarker(
                targetPos,
                new Color("f5a623"),
                radius: 0.4f,
                height: 0.8f));

            _scenarioContainer.AddChild(CreateLine(
                new Vector3(targetPos.X, targetGroundY, targetPos.Z),
                targetPos,
                new Color("f5a623", 0.5f)));

            LineOfSightResult result = _geometry.CheckLineOfSight(preset.Observer, targetNumerics);
            if (result.IsClear)
            {
                _scenarioContainer.AddChild(CreateLine(
                    observerPos,
                    targetPos,
                    new Color("44ff44")));
            }
            else
            {
                Vector3 hitGodot = ToGodot(result.HitPoint);

                _scenarioContainer.AddChild(CreateLine(
                    observerPos,
                    hitGodot,
                    new Color("ff3b30")));

                _scenarioContainer.AddChild(CreateLine(
                    hitGodot,
                    targetPos,
                    new Color(1f, 0.23f, 0.19f, 0.35f)));

                _scenarioContainer.AddChild(CreateMarker(
                    hitGodot,
                    new Color("ff3b30"),
                    radius: 0.25f,
                    height: 0.5f));
            }
        }
    }

    private static MeshInstance3D CreateMarker(Vector3 position, Color color, float radius, float height)
    {
        SphereMesh sphere = new()
        {
            Radius = radius,
            Height = height
        };

        StandardMaterial3D mat = new()
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };

        return new MeshInstance3D
        {
            Mesh = sphere,
            Position = position,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }

    private static MeshInstance3D CreateLine(Vector3 from, Vector3 to, Color color)
    {
        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { from, to };

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }

    private void BuildHud()
    {
        CanvasLayer canvas = new();
        AddChild(canvas);

        PanelContainer top = new()
        {
            Position = new Vector2(24, 24),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        top.AddThemeStyleboxOverride("panel", PanelStyle());

        MarginContainer margin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 18);
        }

        VBoxContainer stack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 8);

        Label title = new()
        {
            Text = "DEAD GROUND   /   LINE-OF-SIGHT GEOMETRY SANDBOX",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(title);

        _presetTitle = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _presetTitle.AddThemeFontSizeOverride("font_size", 16);
        _presetTitle.AddThemeColorOverride("font_color", new Color("ffffff"));
        stack.AddChild(_presetTitle);

        _presetDescription = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _presetDescription.AddThemeFontSizeOverride("font_size", 13);
        _presetDescription.AddThemeColorOverride("font_color", new Color("b2beb1"));
        stack.AddChild(_presetDescription);

        _diagnosticsLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _diagnosticsLabel.AddThemeFontSizeOverride("font_size", 13);
        stack.AddChild(_diagnosticsLabel);

        margin.AddChild(stack);
        top.AddChild(margin);
        canvas.AddChild(top);

        PanelContainer bottom = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.AddChild(bottom);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.OffsetLeft = 24;
        bottom.OffsetRight = -24;
        bottom.OffsetTop = -68;
        bottom.OffsetBottom = -20;
        bottom.AddThemeStyleboxOverride("panel", PanelStyle());

        VBoxContainer instructions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        Label controls = new()
        {
            Text = "  [1..6] Select Preset   /   [ [ / ] ] Prev/Next   /   WASD Pan   /   Wheel Zoom   /   Space Reset View",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controls.AddThemeFontSizeOverride("font_size", 14);
        controls.AddThemeColorOverride("font_color", new Color("edc76b"));
        instructions.AddChild(controls);
        bottom.AddChild(instructions);
    }

    private void UpdateHud(LosSandboxPreset preset)
    {
        _presetTitle.Text = $"PRESET {preset.Id + 1} OF {_presets.Count}: {preset.Name.ToUpperInvariant()}";
        _presetDescription.Text = $"{preset.Description}\nObserver: ({preset.Observer.X:F1}, {preset.Observer.Y:F1}, {preset.Observer.Z:F1}) m";

        System.Text.StringBuilder sb = new();
        for (int i = 0; i < preset.TargetPoints.Length; i++)
        {
            NumericsVector3 target = preset.TargetPoints[i];
            LineOfSightResult result = _geometry.CheckLineOfSight(preset.Observer, target);

            string status = result.IsClear
                ? "CLEAR"
                : result.ObstructionKind == ObstructionKind.Terrain
                    ? "BLOCKED BY TERRAIN"
                    : $"BLOCKED BY STRUCTURE #{result.ObstructingStructureId.Value}";

            sb.Append($"  - Target {i + 1} [{preset.TargetLabels[i]}]: {status}\n");
            sb.Append($"    Distance: {result.Distance:F1} m  |  Ray: {result.RayFraction * 100f:F1}%  |  Hit: ({result.HitPoint.X:F1}, {result.HitPoint.Y:F1}, {result.HitPoint.Z:F1})\n");
        }

        _diagnosticsLabel.Text = sb.ToString().TrimEnd();
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.055f, 0.08f, 0.065f, 0.94f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6
    };

    private static Vector3 ToGodot(NumericsVector3 v) => new(v.X, v.Y, v.Z);
}
