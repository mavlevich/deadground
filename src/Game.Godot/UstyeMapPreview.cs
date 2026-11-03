using System.Diagnostics;
using System.IO;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Standalone 3D visual preview and inspection mode for the 5000 x 5000 m Ustye map.
/// Uses UstyeMapView for shared 3D map presentation.
/// Hosts camera navigation, landmarks, telemetry HUD, benchmarking, and screenshots.
/// </summary>
public partial class UstyeMapPreview : Node3D
{
    public sealed record CameraLandmark(
        string Id,
        string Name,
        Vector3 Focus,
        float Size,
        float Pitch,
        float Yaw);

    public sealed class ScaleVehicle
    {
        public string Id { get; }
        public string? Faction { get; }
        internal VehicleVisual Visual { get; }
        public Vector3 GroundPosition { get; }
        public int ActiveLod { get; private set; }

        internal ScaleVehicle(string id, string? faction, VehicleVisual visual, Vector3 pos)
        {
            Id = id;
            Faction = faction;
            Visual = visual;
            GroundPosition = pos;
            ActiveLod = 0;
        }

        public void UpdateLod(float cameraSize)
        {
            int targetLod = cameraSize < UnitView.Lod1CameraSize ? 0 : cameraSize < UnitView.Lod2CameraSize ? 1 : 2;
            targetLod = Math.Clamp(targetLod, 0, Visual.LodCount - 1);
            if (targetLod == ActiveLod) return;

            Visual.GetOrCreateLod(ActiveLod).Visible = false;
            Visual.GetOrCreateLod(targetLod).Visible = true;
            ActiveLod = targetLod;
        }
    }

    private UstyeMapView _mapView = null!;
    private Camera3D _camera = null!;
    private CameraRig _cameraRig = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;

    private CanvasLayer _hud = null!;
    private Label _titleLabel = null!;
    private Label _landmarkLabel = null!;
    private Label _telemetryLabel = null!;
    private Label _helpLabel = null!;

    private CameraLandmark[] _landmarks = [];
    private int _currentLandmarkIndex;
    private bool _isTransitioning;
    private Vector3 _transitionStartFocus;
    private Vector3 _transitionTargetFocus;
    private float _transitionStartSize;
    private float _transitionTargetSize;
    private float _transitionStartPitch;
    private float _transitionTargetPitch;
    private float _transitionStartYaw;
    private float _transitionTargetYaw;
    private double _transitionElapsed;
    private const double TransitionDuration = 0.8;

    // HUD telemetry throttling (4 Hz = every 0.25s)
    private double _telemetryTimer = 0.25;
    private int _lastTelemetryFocusX = int.MinValue;
    private int _lastTelemetryFocusZ = int.MinValue;
    private int _lastTelemetryElev = int.MinValue;
    private int _lastTelemetrySize = int.MinValue;
    private bool _lastTelemetryPerspective;
    private int _lastTelemetryFps = -1;

    private string? _screenshotPath;
    private double _screenshotDelay;
    private bool _screenshotQuit;
    private string? _captureDir;
    private int _captureStep = -1;
    private double _captureTimer;

    private bool _runBenchmark;
    private int _benchmarkStage;
    private long _benchmarkStageStartTimestamp;
    private long _lastFrameTimestamp;
    private readonly List<double> _overviewFrameTimes = [];
    private readonly List<double> _townFrameTimes = [];
    private readonly List<double> _overviewCpuTimes = [];
    private readonly List<double> _townCpuTimes = [];
    private long _loadTimeMs;

    public UstyePlanData Plan => _mapView.Plan;
    public Node3D TerrainRoot => _mapView.TerrainRoot;
    public Node3D WaterRoot => _mapView.WaterRoot;
    public Node3D RoadsRoot => _mapView.RoadsRoot;
    public Node3D StructuresRoot => _mapView.StructuresRoot;
    public Node3D ContextRoot => _mapView.ContextRoot;
    public Node3D LandmarksRoot => _mapView.LandmarksRoot;
    public Node3D VegetationRoot => _mapView.VegetationRoot;
    public Node3D? VehiclesRoot => _mapView.VehiclesRoot;
    public Mesh ConiferTreeMesh => _mapView.ConiferTreeMesh;
    public Mesh DeciduousTreeMesh => _mapView.DeciduousTreeMesh;
    public IReadOnlyList<MultiMeshInstance3D> ConiferMultiMeshes => _mapView.ConiferMultiMeshes;
    public IReadOnlyList<MultiMeshInstance3D> DeciduousMultiMeshes => _mapView.DeciduousMultiMeshes;
    public int TotalConiferCount => _mapView.TotalConiferCount;
    public int TotalDeciduousCount => _mapView.TotalDeciduousCount;
    public IReadOnlyList<ScaleVehicle> ScaleVehicles => _mapView.ScaleVehicles;
    internal bool IsRiparianChunk(int cx, int cz) => _mapView.IsRiparianChunk(cx, cz);

    public Camera3D Camera => _camera;
    internal CameraRig Rig => _cameraRig;
    public CameraLandmark[] Landmarks => _landmarks;

    internal static float SmoothNoise2D(float x, float z, float frequency)
    {
        float nx = x * frequency;
        float nz = z * frequency;
        float s1 = MathF.Sin(nx * 1.0f + nz * 0.7f);
        float s2 = MathF.Sin(nx * -0.6f + nz * 1.3f + 1.2f);
        float s3 = MathF.Sin(nx * 1.8f - nz * 1.1f + 2.7f);
        return 0.5f + 0.5f * (0.50f * s1 + 0.35f * s2 + 0.15f * s3);
    }

    public override void _Ready()
    {
        Stopwatch sw = Stopwatch.StartNew();
        ParseArgs();

        UstyePlanData plan = UstyePlanData.Load();
        SetupEnvironment();
        SetupCamera(plan);

        _mapView = new UstyeMapView(plan, includeScaleVehicles: true);
        AddChild(_mapView);

        SetupHud();
        SelectLandmark(0, immediate: true);
        sw.Stop();
        _loadTimeMs = sw.ElapsedMilliseconds;

        GD.Print(FormattableString.Invariant(
            $"UstyeMapPreview ready in {_loadTimeMs} ms"));

        if (_runBenchmark)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Godot.Engine.MaxFps = 0;

            _benchmarkStage = 1;
            _benchmarkStageStartTimestamp = Stopwatch.GetTimestamp();
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _overviewFrameTimes.Clear();
            _townFrameTimes.Clear();
            _overviewCpuTimes.Clear();
            _townCpuTimes.Clear();
        }
    }

    private void ParseArgs()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        string[] allArgs = OS.GetCmdlineArgs();
        string[] args = [.. userArgs, .. allArgs];

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--screenshot" && i + 1 < args.Length)
            {
                _screenshotPath = args[i + 1];
                i++;
            }
            else if (args[i] == "--screenshot-delay" && i + 1 < args.Length &&
                     double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                _screenshotDelay = d;
                i++;
            }
            else if (args[i] == "--screenshot-quit")
            {
                _screenshotQuit = true;
            }
            else if (args[i] == "--map-preview-capture-all" && i + 1 < args.Length)
            {
                _captureDir = args[i + 1];
                _captureStep = 0;
                _captureTimer = 0.5;
                i++;
            }
            else if (args[i] == "--benchmark")
            {
                _runBenchmark = true;
            }
        }
    }

    private void SetupEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color("fff3e3"),
            LightEnergy = 1.22f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 1800f,
            DirectionalShadowPancakeSize = 15f,
            RotationDegrees = new Vector3(-48f, -40f, 0f)
        };
        AddChild(_sun);

        Godot.Environment env = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = new Color("527396"),
                    SkyHorizonColor = new Color("8d9fae"),
                    GroundBottomColor = new Color("22241e"),
                    GroundHorizonColor = new Color("7f8d7c"),
                    SunAngleMax = 30f
                }
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("708ba4"),
            AmbientLightEnergy = 0.48f,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Exponential,
            FogDensity = 0.00008f,
            FogLightColor = new Color("8fa2b2"),
            FogLightEnergy = 0.42f
        };

        _environment = new WorldEnvironment
        {
            Name = "Environment",
            Environment = env
        };
        AddChild(_environment);
    }

    private void SetupCamera(UstyePlanData plan)
    {
        _camera = new Camera3D
        {
            Name = "PreviewCamera",
            Current = true,
            Near = 1f,
            Far = 10000f,
            Fov = 55f
        };
        AddChild(_camera);

        Vector3 initialFocus = new(2500f, plan.SampleTerrainElevation(2500f, 2500f), 2500f);
        _cameraRig = new CameraRig(_camera, initialFocus, 4500f, plan.ExtentX, plan.ExtentZ);
        _cameraRig.Model.SetSizeLimits(30f, 5500f);
        _cameraRig.FarClipMargin = 3000f;
        _cameraRig.GroundHeight = (x, z) => plan.SampleTerrainElevation(x, z);

        _landmarks =
        [
            new("overview", "1: Map Overview", new Vector3(2500f, 12f, 2500f), 4600f, 0.90f, 0f),
            new("town", "2: Riverside Town & Central Bridge", new Vector3(2600f, 4.5f, 2450f), 380f, 0.62f, -0.40f),
            new("church", "3: Church Ridge (OBS-W)", new Vector3(1850f, 35f, 1450f), 220f, 0.68f, 0.35f),
            new("sawmill", "4: Sawmill & Water Tower (TIMBER)", new Vector3(2530f, 4.0f, 3680f), 240f, 0.65f, -0.55f),
            new("radio", "5: Radio Hill (OBS-E)", new Vector3(3750f, 45f, 3370f), 240f, 0.68f, -0.4f),
            new("lighthouse", "6: Coastal Lighthouse (LIGHT)", new Vector3(4380f, 10f, 3950f), 220f, 0.65f, 0.5f),
            new("cross_n", "7: Northern Crossing (CROSS-N)", new Vector3(2400f, 4.2f, 1000f), 380f, 0.60f, -0.55f),
            new("cross_s", "8: Southern Crossing (CROSS-S)", new Vector3(2860f, 4.2f, 3550f), 220f, 0.72f, -0.25f),
            new("depot", "9: Ammo Depot (DEPOT)", new Vector3(3510f, 4f, 2730f), 250f, 0.70f, 0.2f),
            new("entry_a", "0: Southwest Entry (ENTRY-A)", new Vector3(500f, 4f, 4400f), 280f, 0.75f, 0f)
        ];
    }

    private void SetupHud()
    {
        _hud = new CanvasLayer { Name = "HUD" };
        AddChild(_hud);

        PanelContainer topPanel = new()
        {
            Position = new Vector2(16, 12),
            Size = new Vector2(580, 84),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hud.AddChild(topPanel);

        VBoxContainer topBox = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        topPanel.AddChild(topBox);

        _titleLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "USTYE: BACKWATER CROSSING | 5000 x 5000 m PREVIEW"
        };
        topBox.AddChild(_titleLabel);

        _landmarkLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "Orient: 1: Map Overview"
        };
        topBox.AddChild(_landmarkLabel);

        _telemetryLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "Focus: [2500, 2500] | Ground: 4.0 m | Size: 4500 m | Mode: Perspective | FPS: 60"
        };
        topBox.AddChild(_telemetryLabel);

        PanelContainer bottomPanel = new()
        {
            Position = new Vector2(16, 830),
            Size = new Vector2(1000, 48),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hud.AddChild(bottomPanel);

        _helpLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "WASD/Arrows: Pan | Wheel: Zoom | MMB/RMB/Alt+Drag: Orbit | V: Projection | 1-9, 0: Jump to Landmark | Esc: Exit"
        };
        bottomPanel.AddChild(_helpLabel);

        UpdateLandmarkHud();
        UpdateTelemetryHud(force: true);
    }

    public void SelectLandmark(int index, bool immediate = false)
    {
        if (index < 0 || index >= _landmarks.Length) return;

        _currentLandmarkIndex = index;
        CameraLandmark lm = _landmarks[index];

        if (immediate)
        {
            _isTransitioning = false;
            _cameraRig.CenterOn(lm.Focus);
            _cameraRig.Model.SetZoom(lm.Size);
            _cameraRig.Orbit(lm.Yaw - _cameraRig.ViewYaw, lm.Pitch - _cameraRig.ViewPitch);
        }
        else
        {
            _isTransitioning = true;
            _transitionElapsed = 0.0;
            _transitionStartFocus = _cameraRig.Focus;
            _transitionTargetFocus = lm.Focus;
            _transitionStartSize = _cameraRig.Size;
            _transitionTargetSize = lm.Size;
            _transitionStartPitch = _cameraRig.ViewPitch;
            _transitionTargetPitch = lm.Pitch;
            _transitionStartYaw = _cameraRig.ViewYaw;
            _transitionTargetYaw = lm.Yaw;
        }

        UpdateLandmarkHud();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_runBenchmark || _captureDir is not null || _screenshotPath is not null)
            return;

        if (_cameraRig.HandleZoomInput(@event))
        {
            _isTransitioning = false;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseMotion motion &&
            ((motion.ButtonMask & (MouseButtonMask.Middle | MouseButtonMask.Right)) != 0 ||
             (motion.AltPressed && (motion.ButtonMask & MouseButtonMask.Left) != 0)))
        {
            _isTransitioning = false;
            _cameraRig.DragOrbit(motion.Relative);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode == Key.Escape)
        {
            GetTree().Quit();
        }
        else if (key.Keycode == Key.V)
        {
            _isTransitioning = false;
            _cameraRig.SetPerspective(!_cameraRig.IsPerspective);
        }
        else
        {
            int landmarkIndex = key.Keycode switch
            {
                Key.Key1 => 0,
                Key.Key2 => 1,
                Key.Key3 => 2,
                Key.Key4 => 3,
                Key.Key5 => 4,
                Key.Key6 => 5,
                Key.Key7 => 6,
                Key.Key8 => 7,
                Key.Key9 => 8,
                Key.Key0 => 9,
                _ => -1
            };
            if (landmarkIndex < 0)
                return;
            SelectLandmark(landmarkIndex);
        }

        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        long processStartTimestamp = Stopwatch.GetTimestamp();

        if (_isTransitioning)
        {
            _transitionElapsed += delta;
            float t = (float)Math.Clamp(_transitionElapsed / TransitionDuration, 0.0, 1.0);
            float smoothT = Mathf.SmoothStep(0f, 1f, t);

            Vector3 currentFocus = _transitionStartFocus.Lerp(_transitionTargetFocus, smoothT);
            float currentSize = Mathf.Lerp(_transitionStartSize, _transitionTargetSize, smoothT);
            float currentPitch = Mathf.Lerp(_transitionStartPitch, _transitionTargetPitch, smoothT);
            float currentYaw = Mathf.Lerp(_transitionStartYaw, _transitionTargetYaw, smoothT);

            _cameraRig.CenterOn(currentFocus);
            _cameraRig.Model.SetZoom(currentSize);
            _cameraRig.Orbit(currentYaw - _cameraRig.ViewYaw, currentPitch - _cameraRig.ViewPitch);

            if (t >= 1.0f)
            {
                _isTransitioning = false;
            }
        }

        _cameraRig.Update(delta, allowInput: !_isTransitioning && !_runBenchmark);
        _mapView.UpdateVehicleLods(_cameraRig.Size);

        _telemetryTimer += delta;
        if (_telemetryTimer >= 0.25)
        {
            _telemetryTimer = 0.0;
            UpdateTelemetryHud(force: false);
        }

        long processEndTimestamp = Stopwatch.GetTimestamp();
        double cpuTimeMs = (double)(processEndTimestamp - processStartTimestamp) / Stopwatch.Frequency * 1000.0;

        if (_runBenchmark)
        {
            ProcessBenchmarkMonotonic(cpuTimeMs);
        }
        else
        {
            HandleCaptures(delta);
        }
    }

    private void UpdateLandmarkHud()
    {
        if (_landmarkLabel is null || _currentLandmarkIndex < 0 || _currentLandmarkIndex >= _landmarks.Length) return;
        _landmarkLabel.Text = $"Orient: {_landmarks[_currentLandmarkIndex].Name}";
    }

    private void UpdateTelemetryHud(bool force)
    {
        if (_telemetryLabel is null || _cameraRig is null) return;

        Vector3 focus = _cameraRig.Focus;
        int fx = (int)MathF.Round(focus.X);
        int fz = (int)MathF.Round(focus.Z);
        float elev = _mapView.Plan.SampleTerrainElevation(focus.X, focus.Z);
        int elevInt = (int)MathF.Round(elev);
        int sizeInt = (int)MathF.Round(_cameraRig.Size);
        bool isPersp = _cameraRig.IsPerspective;
        int fps = (int)Math.Round(Godot.Engine.GetFramesPerSecond());

        if (!force && fx == _lastTelemetryFocusX && fz == _lastTelemetryFocusZ &&
            elevInt == _lastTelemetryElev && sizeInt == _lastTelemetrySize &&
            isPersp == _lastTelemetryPerspective && fps == _lastTelemetryFps)
        {
            return;
        }

        _lastTelemetryFocusX = fx;
        _lastTelemetryFocusZ = fz;
        _lastTelemetryElev = elevInt;
        _lastTelemetrySize = sizeInt;
        _lastTelemetryPerspective = isPersp;
        _lastTelemetryFps = fps;

        string mode = isPersp ? "Perspective" : "Orthogonal";
        _telemetryLabel.Text = FormattableString.Invariant(
            $"Focus: [{fx}, {fz}] | Ground: {elev:F1} m | Size: {sizeInt} m | Mode: {mode} | FPS: {fps}");
    }

    private void ProcessBenchmarkMonotonic(double cpuTimeMs)
    {
        long now = Stopwatch.GetTimestamp();
        double frameTimeMs = (double)(now - _lastFrameTimestamp) / Stopwatch.Frequency * 1000.0;
        _lastFrameTimestamp = now;

        double stageElapsedSec = (double)(now - _benchmarkStageStartTimestamp) / Stopwatch.Frequency;

        switch (_benchmarkStage)
        {
            case 1:
                SelectLandmark(0, immediate: true);
                if (stageElapsedSec >= 1.0)
                {
                    _benchmarkStage = 2;
                    _benchmarkStageStartTimestamp = now;
                    _overviewFrameTimes.Clear();
                    _overviewCpuTimes.Clear();
                }
                break;

            case 2:
                _overviewFrameTimes.Add(frameTimeMs);
                _overviewCpuTimes.Add(cpuTimeMs);
                if (stageElapsedSec >= 3.0)
                {
                    _benchmarkStage = 3;
                    _benchmarkStageStartTimestamp = now;
                    SelectLandmark(1, immediate: true);
                }
                break;

            case 3:
                if (stageElapsedSec >= 1.0)
                {
                    _benchmarkStage = 4;
                    _benchmarkStageStartTimestamp = now;
                    _townFrameTimes.Clear();
                    _townCpuTimes.Clear();
                }
                break;

            case 4:
                _townFrameTimes.Add(frameTimeMs);
                _townCpuTimes.Add(cpuTimeMs);
                if (stageElapsedSec >= 3.0)
                {
                    _benchmarkStage = 5;
                    PrintAndSaveBenchmarkReport();
                    GetTree().Quit();
                }
                break;
        }
    }

    private void PrintAndSaveBenchmarkReport()
    {
        double ovMedian = Median(_overviewFrameTimes);
        double ovP95 = Percentile(_overviewFrameTimes, 0.95);
        double ovFps = 1000.0 / Math.Max(0.001, ovMedian);
        double ovCpu = Median(_overviewCpuTimes);

        double townMedian = Median(_townFrameTimes);
        double townP95 = Percentile(_townFrameTimes, 0.95);
        double townFps = 1000.0 / Math.Max(0.001, townMedian);
        double townCpu = Median(_townCpuTimes);

        int totalTris = 0;
        foreach (Node child in _mapView.TerrainRoot.GetChildren())
        {
            if (child is MeshInstance3D mi && mi.Mesh is ArrayMesh am)
            {
                using Godot.Collections.Array arr = am.SurfaceGetArrays(0);
                int[] idx = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                totalTris += idx.Length / 3;
            }
        }

        string report = FormattableString.Invariant($"""
        ================================================================================
        USTYE MAP PREVIEW BENCHMARK REPORT
        ================================================================================
        Map Scale: 5000 x 5000 m (25 km2)
        Load Time: {_loadTimeMs} ms
        Terrain Mesh: 100 chunks, {totalTris:N0} triangles total
        Vegetation Instances: {TotalConiferCount:N0} conifers, {TotalDeciduousCount:N0} deciduous ({TotalConiferCount + TotalDeciduousCount:N0} total)

        Phase 1 - Map Overview (Height ~4500 m, full map in FOV):
          FPS: {ovFps:F1} fps
          Frame Time (Median): {ovMedian:F2} ms
          Frame Time (p95): {ovP95:F2} ms
          CPU Script Time (Median): {ovCpu:F3} ms
          Frame Count Sampled: {_overviewFrameTimes.Count}

        Phase 2 - Riverside Town & Central Bridge (Height ~380 m, close inspection):
          FPS: {townFps:F1} fps
          Frame Time (Median): {townMedian:F2} ms
          Frame Time (p95): {townP95:F2} ms
          CPU Script Time (Median): {townCpu:F3} ms
          Frame Count Sampled: {_townFrameTimes.Count}

        Status: {(ovFps >= 45.0 && townFps >= 45.0 ? "PASSED (>= 45 fps target)" : "BELOW TARGET")}
        ================================================================================
        """);

        GD.Print(report);

        try
        {
            string outPath = Path.Combine(Directory.GetCurrentDirectory(), "docs/maps/ustye/benchmark-results.md");
            File.WriteAllText(outPath, FormattableString.Invariant($"""
            # Ustye 5000 x 5000 m Map Preview Benchmark Results

            Tested on: {OS.GetName()} ({System.Environment.Version})
            Vsync: Disabled
            Framerate Cap: None

            ## Scene Metrics
            - Extent: 5000 x 5000 m (25 km2)
            - Terrain Grid: 10 x 10 chunks (80 inland chunks @ 25m step, 20 riparian chunks @ 6.25m step)
            - Terrain Triangles: {totalTris:N0}
            - MultiMesh Foliage: {TotalConiferCount:N0} conifers + {TotalDeciduousCount:N0} deciduous = {TotalConiferCount + TotalDeciduousCount:N0} total
            - Load Time: {_loadTimeMs} ms

            ## Performance Results

            | Viewpoint | Median FPS | Median Frame Time | p95 Frame Time | Median CPU Time | Status |
            | :--- | :---: | :---: | :---: | :---: | :---: |
            | 1: Map Overview (4600m) | {ovFps:F1} | {ovMedian:F2} ms | {ovP95:F2} ms | {ovCpu:F3} ms | {(ovFps >= 45.0 ? "PASS" : "WARN")} |
            | 2: Riverside Town (380m) | {townFps:F1} | {townMedian:F2} ms | {townP95:F2} ms | {townCpu:F3} ms | {(townFps >= 45.0 ? "PASS" : "WARN")} |
            """));
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Could not write benchmark file: {ex.Message}");
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        List<double> sorted = [.. values];
        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) * 0.5 : sorted[mid];
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0.0;
        List<double> sorted = [.. values];
        sorted.Sort();
        int idx = (int)Math.Clamp(Math.Round(sorted.Count * p), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private void HandleCaptures(double delta)
    {
        if (_screenshotPath is not null)
        {
            if (_screenshotDelay > 0)
            {
                _screenshotDelay -= delta;
                return;
            }

            CaptureViewportToFile(_screenshotPath);
            _screenshotPath = null;
            if (_screenshotQuit)
            {
                GetTree().Quit();
            }
            return;
        }

        if (_captureDir is not null)
        {
            _captureTimer -= delta;
            if (_captureTimer <= 0)
            {
                if (_captureStep < _landmarks.Length)
                {
                    SelectLandmark(_captureStep, immediate: true);
                    CameraLandmark lm = _landmarks[_captureStep];
                    string filePath = Path.Combine(_captureDir, $"{_captureStep + 1:D2}_{lm.Id}.png");
                    CaptureViewportToFile(filePath);
                    _captureStep++;
                    _captureTimer = 0.5;
                }
                else
                {
                    _captureDir = null;
                    if (_screenshotQuit)
                    {
                        GetTree().Quit();
                    }
                }
            }
        }
    }

    private void CaptureViewportToFile(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
        Image image = GetViewport().GetTexture().GetImage();
        image.SavePng(filePath);
        GD.Print($"Saved preview capture: {filePath}");
    }
}
