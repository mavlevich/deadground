using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

public partial class Main : Node3D
{
    private HeightMap _map = null!;
    private IGameSession _session = null!;
    private Camera3D _camera = null!;
    private CameraRig _cameraRig = null!;
    private SettingsPanel _settingsPanel = null!;
    private FramePacingMetrics? _benchmarkMetrics;
    private int _benchmarkTargetFrames;
    private long _lastFrameTimestamp;
    private int _benchmarkWarmupFrames = 5;
    private Label _stats = null!, _status = null!, _subtitle = null!, _title = null!;
    private UnitViewSet _unitViews = null!;
    private ContactViewSet _contactViews = null!;
    private WreckViewSet _wreckViews = null!;
    private OrderDestinationView _destination = null!;
    private OrderCursorOverlay _orderCursorOverlay = null!;
    private OrderPreviewController _previewController = null!;
    private DestinationEvaluator _destinationEvaluator = null!;
    private double _hudTimer, _smokeTimer;
    private bool _smokeTest;
    private int _smokeQuitFrames = -1;
    private int _smokeExitCode;
    private bool _terrainWindingValid;
    private long _lastBytes;
    private uint _lastCommandResult;
    private string _scenario = "prototype";
    private CombatEffects _combatEffects = null!;
    private MissionPanel? _missionPanel;
    private StaticGeometry? _scenarioGeometry;
    private UnitCatalog _catalog = null!;
    private SelectionCard _selectionCard = null!;
    private OrderNotification _orderNotification = null!;
    private RoleBadgeOverlay _roleBadgeOverlay = null!;
    private SelectionBoxOverlay _selectionBoxOverlay = null!;
    private readonly SelectionController _selectionController = new();
    private PanelContainer _topHud = null!, _bottomHud = null!;
    private TacticalMinimap _minimap = null!;
    private readonly Rect2[] _badgeOccludingPanels = new Rect2[7];
    private readonly Rect2[] _inputOccludingPanels = new Rect2[6];
    private FogOfWarView? _fogOfWar;
    private string? _screenshotPath;
    private double _screenshotTimer = 0.5;
    private string? _screenshotOnFinishPath;
    private double _screenshotOnFinishTimer = 1.0;
    private bool _screenshotQuit;
    private string? _forestDemoCaptureDir;
    private int _forestDemoStage = -1;
    private double _forestDemoTimer = 0.5;
    private bool _forestDemoTargetSpotted;
    private bool _forestDemoContactLost;
    private bool _showVehicleCollisionBounds;
    private readonly MeshInstance3D _collisionDebugMeshInstance = new();
    private readonly ImmediateMesh _collisionDebugMesh = new();
    private bool _showVegetationDebug;
    private readonly MeshInstance3D _vegetationDebugMeshInstance = new();
    private readonly ImmediateMesh _vegetationDebugMesh = new();
    private static readonly StandardMaterial3D VegetationDebugMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private static readonly StandardMaterial3D CollisionBoundsMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private static readonly StandardMaterial3D VelocityDebugMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.2f, 0.9f, 1.0f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private bool _trafficObservedWaiting;
    private bool _trafficObservedResumed;
    private bool _trafficLeaderCleared;
    private int _ustyeSetupIndex;
    private UstyeMapView? _ustyeMapView;
    private bool _hasExplicitCameraFocus;
    private Vector3 _initialSpawnFocus;
    private float _initialSpawnSize;
    private int _homeViewAssignedPlayerId;
    private SessionMenu _sessionMenu = null!;
    private PauseMenu _pauseMenu = null!;
    private ClientLifecycleController _lifecycleController = null!;
    private bool _isMenuMode;
    private CanvasLayer _hudCanvas = null!;
    private MatchSetup _matchSetup = null!;
    private readonly Node3D _environmentRoot = new() { Name = "EnvironmentRoot" };

    internal FogOfWarView? FogOfWar => _fogOfWar;
    internal CameraRig CameraRig => _cameraRig;
    internal Camera3D Camera => _camera;
    internal SettingsPanel SettingsPanel => _settingsPanel;
    internal SessionMenu SessionMenu => _sessionMenu;
    internal PauseMenu PauseMenu => _pauseMenu;
    internal ClientLifecycleController LifecycleController => _lifecycleController;
    internal bool IsMenuMode => _isMenuMode;
    internal CanvasLayer HudCanvas => _hudCanvas;
    internal MissionPanel? MissionPanel => _missionPanel;
    internal SelectionCard SelectionCard => _selectionCard;
    internal OrderNotification OrderNotification => _orderNotification;
    internal RoleBadgeOverlay RoleBadgeOverlay => _roleBadgeOverlay;
    internal SelectionBoxOverlay SelectionBoxOverlay => _selectionBoxOverlay;
    internal SelectionController SelectionController => _selectionController;
    internal TacticalMinimap Minimap => _minimap;
    internal ReadOnlySpan<Rect2> InputOccludingPanels => _inputOccludingPanels;
    internal ReadOnlySpan<Rect2> BadgeOccludingPanels => _badgeOccludingPanels;
    internal UnitViewSet UnitViews => _unitViews;
    internal ContactViewSet ContactViews => _contactViews;
    internal WreckViewSet WreckViews => _wreckViews;
    internal IGameSession Session { get => _session; set => _session = value; }
    internal UnitCatalog Catalog => _catalog;
    internal CombatEffects CombatEffects => _combatEffects;
    internal OrderDestinationView DestinationMarker => _destination;
    internal HeightMap Map => _map;
    internal string Scenario => _scenario;
    internal OrderCursorOverlay OrderCursorOverlay => _orderCursorOverlay;
    internal OrderPreviewController PreviewController => _previewController;
    internal DestinationEvaluator DestinationEvaluator => _destinationEvaluator;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        string[] allArgs = OS.GetCmdlineArgs();
        bool isLosSandbox = userArgs.Contains("--los-sandbox") || allArgs.Contains("--los-sandbox");
        bool isVehiclePreview = userArgs.Contains("--vehicle-preview") || allArgs.Contains("--vehicle-preview");
        string? mapPreviewTarget = null;
        for (int i = 0; i < userArgs.Length; i++)
        {
            if (userArgs[i] == "--map-preview" && i + 1 < userArgs.Length)
            {
                mapPreviewTarget = userArgs[i + 1].ToLowerInvariant();
                break;
            }
        }
        if (mapPreviewTarget is null)
        {
            for (int i = 0; i < allArgs.Length; i++)
            {
                if (allArgs[i] == "--map-preview" && i + 1 < allArgs.Length)
                {
                    mapPreviewTarget = allArgs[i + 1].ToLowerInvariant();
                    break;
                }
            }
        }
        bool isMapPreview = mapPreviewTarget is not null;
        bool hasConnect = userArgs.Contains("--connect") || allArgs.Contains("--connect");

        if ((isLosSandbox || isVehiclePreview || isMapPreview) && hasConnect)
        {
            string flag = isVehiclePreview ? "--vehicle-preview" : isLosSandbox ? "--los-sandbox" : "--map-preview";
            GD.PushError($"Invalid arguments: {flag} cannot be combined with --connect.");
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }

        if (isMapPreview)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            if (mapPreviewTarget != "ustye")
            {
                GD.PushError($"Unknown map preview target: '{mapPreviewTarget}'. Available: ustye");
                GetTree().Quit(1);
                return;
            }

            UstyeMapPreview preview = new();
            AddChild(preview);

            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = UstyeMapPreviewRegressionTests.Run(preview);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            return;
        }

        if (isLosSandbox)
        {
            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = LosSandboxRegressionTests.Run();
                SetProcess(false);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            SetProcess(false);
            SetProcessUnhandledInput(false);
            AddChild(new LosSandboxView());
            return;
        }

        if (isVehiclePreview)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = VehiclePreviewRegressionTests.Run(this);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            AddChild(new VehiclePreviewView());
            return;
        }

        bool isPerfMicrobench = userArgs.Contains("--perf-microbench") || allArgs.Contains("--perf-microbench");
        if (isPerfMicrobench)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            Node3D benchRoot = new();
            AddChild(benchRoot);
            _ = ClientPerfProbe.RunMicrobenchmark(benchRoot).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"CLIENT_PERF_MICROBENCH_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        _smokeTest = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");

        bool isCombatProbe = userArgs.Contains("--combat-feedback-probe") || allArgs.Contains("--combat-feedback-probe");
        if (isCombatProbe)
        {
            SetProcess(false);
            _ = CombatFeedbackProbe.Run(this).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"COMBAT_FEEDBACK_PROBE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        string? connectTarget = null;
        string? captureDir = null;
        string? effectsCaptureDir = null;
        string? orderPreviewCaptureDir = null;
        float? overrideCameraSize = null;
        Vector3? overrideCameraFocus = null;
        int port = 7777;
        bool enemyHoldFire = false;
        bool noPrewarm = false;
        bool suspendFogCalc = false;
        bool minimapDemo = false;
        bool showMultiplayerMenu = false;
        string? menuErrorMessage = null;
        string? menuConnectionLostReason = null;

        void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-prewarm")
                {
                    noPrewarm = true;
                }
                else if (args[i] == "--suspend-fog-calc")
                {
                    suspendFogCalc = true;
                }
                else if (args[i] == "--connect" && i + 1 < args.Length)
                {
                    connectTarget = args[i + 1];
                    i++;
                }
                else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    port = p;
                    i++;
                }
                else if (args[i] == "--scenario" && i + 1 < args.Length)
                {
                    _scenario = args[i + 1].ToLowerInvariant();
                    i++;
                }
                else if (args[i] == "--setup" && i + 1 < args.Length && int.TryParse(args[i + 1], out int sIdx))
                {
                    _ustyeSetupIndex = Math.Clamp(sIdx, 0, 3);
                    i++;
                }
                else if (args[i] == "--screenshot" && i + 1 < args.Length)
                {
                    _screenshotPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--screenshot-on-finish" && i + 1 < args.Length)
                {
                    _screenshotOnFinishPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--screenshot-delay" && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double delay))
                {
                    _screenshotTimer = delay;
                    i++;
                }
                else if (args[i] == "--screenshot-quit")
                {
                    _screenshotQuit = true;
                }
                else if (args[i] == "--forest-demo-capture" && i + 1 < args.Length)
                {
                    _forestDemoCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--enemy-hold-fire")
                {
                    enemyHoldFire = true;
                }
                else if (args[i] == "--camera-size" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float cSize))
                {
                    overrideCameraSize = cSize;
                    i++;
                }
                else if (args[i] == "--camera-focus" && i + 1 < args.Length)
                {
                    string[] parts = args[i + 1].Split(',');
                    if (parts.Length == 2 &&
                        float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float fx) &&
                        float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float fz))
                    {
                        overrideCameraFocus = new Vector3(fx, 0, fz);
                    }
                    else if (parts.Length == 3 &&
                        float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float fx3) &&
                        float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float fy3) &&
                        float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float fz3))
                    {
                        overrideCameraFocus = new Vector3(fx3, fy3, fz3);
                    }
                    i++;
                }
                else if (args[i] == "--vehicle-effects-capture" && i + 1 < args.Length)
                {
                    effectsCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--order-preview-capture" && i + 1 < args.Length)
                {
                    orderPreviewCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--combat-feedback-capture" && i + 1 < args.Length)
                {
                    captureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--minimap-demo")
                {
                    minimapDemo = true;
                }
                else if (args[i] == "--menu-multiplayer")
                {
                    showMultiplayerMenu = true;
                }
                else if (args[i] == "--menu-error" && i + 1 < args.Length)
                {
                    showMultiplayerMenu = true;
                    menuErrorMessage = args[i + 1];
                    i++;
                }
                else if (args[i] == "--menu-connection-lost" && i + 1 < args.Length)
                {
                    menuConnectionLostReason = args[i + 1];
                    i++;
                }
            }
        }

        ParseArgs(allArgs);
        ParseArgs(userArgs);

        _isMenuMode = !ClientLifecycleController.HasExplicitCliMode(userArgs, allArgs);

        StaticGeometry? scenarioGeometry = null;
        MatchSetup setup;

        if (_isMenuMode)
        {
            _scenario = "battle";
            scenarioGeometry = BattleScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
            _session = new NullSession();
        }
        else
        {
            if (_scenario == "visibility")
            {
                scenarioGeometry = VisibilityScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = VisibilityScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "navigation")
            {
                scenarioGeometry = NavigationScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = NavigationScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "navigation-large")
            {
                scenarioGeometry = LargeNavigationScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = LargeNavigationScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "combat")
            {
                scenarioGeometry = CombatScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = CombatScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "battle")
            {
                scenarioGeometry = BattleScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "recon-raid")
            {
                scenarioGeometry = ReconRaidScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = ReconRaidScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "bridge-crossing")
            {
                scenarioGeometry = BridgeCrossingScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = BridgeCrossingScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "vehicle-traffic")
            {
                scenarioGeometry = VehicleTrafficScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = VehicleTrafficScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "group-move")
            {
                scenarioGeometry = GroupMoveScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = GroupMoveScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "ustye-ground")
            {
                scenarioGeometry = UstyeGroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = UstyeGroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "ustye-raid")
            {
                scenarioGeometry = UstyeRaidScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = UstyeRaidScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "forest-visibility")
            {
                scenarioGeometry = ForestVisibilityScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = ForestVisibilityScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "infantry-playground")
            {
                scenarioGeometry = InfantryPlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = InfantryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else
            {
                _map = HeightMap.CreatePrototype();
                setup = PrototypeScenario.CreateMatchSetup(_map);
            }
            if (scenarioGeometry is null)
            {
                scenarioGeometry = new StaticGeometry(_map, ReadOnlySpan<OpaqueStructure>.Empty);
            }
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);

            if (connectTarget is not null)
            {
                string host = connectTarget;
                if (connectTarget.Contains(':'))
                {
                    string[] parts = connectTarget.Split(':');
                    host = parts[0];
                    if (int.TryParse(parts[1], out int p)) port = p;
                }
                _session = ClientSession.Connect(host, port, setup);
            }
            else
            {
                Simulation localSimulation = _scenario switch
                {
                    "visibility" => VisibilityScenario.CreateSimulation(scenarioGeometry!),
                    "navigation" => NavigationScenario.CreateSimulation(scenarioGeometry!),
                    "navigation-large" => LargeNavigationScenario.CreateSimulation(scenarioGeometry!),
                    "combat" => CombatScenario.CreateSimulation(scenarioGeometry!),
                    "recon-raid" => ReconRaidScenario.CreateSimulation(scenarioGeometry!),
                    "battle" => BattleScenario.CreateSimulation(scenarioGeometry!),
                    "bridge-crossing" => BridgeCrossingScenario.CreateSimulation(scenarioGeometry!),
                    "vehicle-traffic" => VehicleTrafficScenario.CreateSimulation(scenarioGeometry!),
                    "group-move" => GroupMoveScenario.CreateSimulation(scenarioGeometry!),
                    "ustye-ground" => UstyeGroundScenario.CreateSimulation(scenarioGeometry!, setupIndex: _ustyeSetupIndex),
                    "ustye-raid" => UstyeRaidScenario.CreateSimulation(scenarioGeometry!),
                    "forest-visibility" => ForestVisibilityScenario.CreateSimulation(scenarioGeometry!),
                    "infantry-playground" => InfantryPlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    _ => PrototypeScenario.Create(_map)
                };
                // Test switch: the enemy holds fire, so own units can be tried out without losing them.
                if (enemyHoldFire) TestOptions.HoldFireForOthers(localSimulation, 1);
                _session = new LocalSession(localSimulation);
            }
        }


        AddChild(_environmentRoot);
        _fogOfWar = new FogOfWarView(_map, scenarioGeometry);
        BuildEnvironment(scenarioGeometry);

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
                BackgroundColor = new Color("17211c"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("b9c6b2"),
                AmbientLightEnergy = 0.55f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic
            }
        });
        DisplaySettings loadedConfig = SettingsPanel.LoadConfig();
        string[] combinedArgs = allArgs.Concat(userArgs).ToArray();
        bool cliOverridesVsync = DisplaySettings.HasVsyncOverride(combinedArgs);
        bool cliOverridesMaxFps = DisplaySettings.HasMaxFpsOverride(combinedArgs);
        DisplaySettings activeSettings = DisplaySettings.ParseCommandLine(combinedArgs, loadedConfig);

        _settingsPanel = new SettingsPanel();
        AddChild(_settingsPanel);
        _settingsPanel.Initialize(loadedConfig, activeSettings, cliOverridesVsync, cliOverridesMaxFps);

        if (activeSettings.BenchmarkFrames > 0)
        {
            _benchmarkMetrics = new FramePacingMetrics(activeSettings.BenchmarkFrames + 16);
            _benchmarkTargetFrames = activeSettings.BenchmarkFrames;
        }

        float initialCameraSize = overrideCameraSize ?? _scenario switch
        {
            "combat" => 75,
            "navigation-large" => 450,
            "recon-raid" => 160,
            "battle" => 160,
            "bridge-crossing" => 120,
            "vehicle-traffic" => 150,
            "group-move" => 140,
            "ustye-ground" => 220,
            "ustye-raid" => 160,
            "forest-visibility" => 150,
            "infantry-playground" => 120,
            _ => 112
        };
        _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = initialCameraSize, Far = 3500, Current = true };
        AddChild(_camera);
        Vector3 initialFocus = overrideCameraFocus ?? _scenario switch
        {
            "combat" => new Vector3(45, 0, 45),
            "navigation-large" => new Vector3(300, 0, 300),
            "recon-raid" => new Vector3(100, 0, 100),
            "battle" => new Vector3(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y),
            "bridge-crossing" => new Vector3(100, 4, 100),
            "vehicle-traffic" => new Vector3(100, 0, 100),
            "group-move" => new Vector3(60, 0, 50),
            "ustye-ground" => new Vector3(2550f, 4.5f, 2450f),
            "ustye-raid" => new Vector3(2160f, 10f, 2325f),
            "forest-visibility" => new Vector3(100f, 0f, 110f),
            "infantry-playground" => new Vector3(130f, 0f, 180f),
            _ => new Vector3(64, 0, 64)
        };
        _hasExplicitCameraFocus = overrideCameraFocus.HasValue;
        _initialSpawnFocus = initialFocus;
        _initialSpawnSize = initialCameraSize;
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        if (_scenario == "ustye-ground" || _scenario == "ustye-raid")
        {
            _cameraRig.Model.SetSizeLimits(30f, 5500f);
            _cameraRig.FarClipMargin = 3000f;
            _cameraRig.GroundHeight = (x, z) => _map.GetHeight(x, z);
        }
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);

        // Historical scenarios use the Cold War catalog; the small test maps keep prototype data.
        _catalog = ScenarioContent.LoadCatalog(_scenario);
        if (!noPrewarm)
        {
            VehicleVisualCatalog.PrewarmScenario(setup, _catalog);
        }
        if (suspendFogCalc && _fogOfWar is not null)
        {
            _fogOfWar.IsCalculationSuspended = true;
        }
        if (_session.Replica.OwnUnits.Length > 0)
        {
            _fogOfWar?.UpdateAllNow(_session.Replica, _catalog);
        }
        // Factions are part of the public match setup; the side is known once the session assigns it.
        MatchSetup matchSetup = _matchSetup;
        _unitViews = new UnitViewSet(this, _map, _catalog, () => matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _contactViews = new ContactViewSet(this, _map, _catalog, () => matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        AddChild(_combatEffects);

        if (!_isMenuMode)
        {
            if (_session is LocalSession && _scenario == "prototype")
            {
                foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
                {
                    // Demo movement is sent as normal client commands through the byte transport.
                    _session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
                }
                if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "combat" && _session.Replica.OwnUnits.Length > 1)
            {
                // Auto-select first tank for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[1].Id);
            }
            else if (_scenario == "navigation-large" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select first recon unit for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "recon-raid" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select player recon unit for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "bridge-crossing" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select player tank for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "vehicle-traffic" && _session.Replica.OwnUnits.Length > 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[1].Id);
            }
            else if (_scenario == "group-move" && _session.Replica.OwnUnits.Length >= 10)
            {
                for (int i = 0; i < 10; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-ground" && _session.Replica.OwnUnits.Length >= 6)
            {
                for (int i = 0; i < 6; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-raid" && _session.Replica.OwnUnits.Length >= 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "forest-visibility" && _session.Replica.OwnUnits.Length >= 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "infantry-playground" && _session.Replica.OwnUnits.Length >= 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
        }

        _destination = new OrderDestinationView();
        AddChild(_destination);
        _collisionDebugMeshInstance.Mesh = _collisionDebugMesh;
        AddChild(_collisionDebugMeshInstance);
        _vegetationDebugMeshInstance.Mesh = _vegetationDebugMesh;
        AddChild(_vegetationDebugMeshInstance);
        BuildHud();
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);
        UpdateViews();

        _lifecycleController = new ClientLifecycleController();
        _lifecycleController.OnSessionReady += session => EnterGameSession(session);
        _lifecycleController.OnConnectionFailed += err => _sessionMenu.SetStatus(err, ClientLifecycleState.Failed);
        _lifecycleController.OnConnectionLost += err => OnNetworkSessionDisconnected(err);

        _sessionMenu = new SessionMenu();
        AddChild(_sessionMenu);
        _sessionMenu.LocalBattleRequested += StartLocalBattle;
        _sessionMenu.ConnectRequested += StartMultiplayerConnect;
        _sessionMenu.CancelRequested += CancelMultiplayerConnect;
        _sessionMenu.SettingsRequested += () => _settingsPanel.Toggle();
        _sessionMenu.ExitRequested += () => GetTree().Quit();

        _pauseMenu = new PauseMenu();
        AddChild(_pauseMenu);
        _pauseMenu.ResumeRequested += () => { };
        _pauseMenu.SettingsRequested += () => _settingsPanel.Toggle();
        _pauseMenu.LeaveRequested += LeaveCurrentMatch;
        _pauseMenu.ExitRequested += () => GetTree().Quit();

        if (_isMenuMode)
        {
            _hudCanvas.Visible = false;
            if (showMultiplayerMenu)
            {
                _sessionMenu.ShowMultiplayerView();
                if (menuErrorMessage is not null)
                {
                    _sessionMenu.SetStatus(menuErrorMessage, ClientLifecycleState.Failed);
                }
            }
            else if (menuConnectionLostReason is not null)
            {
                _sessionMenu.ShowConnectionLostModal(menuConnectionLostReason);
            }
            else
            {
                _sessionMenu.ShowMainMenuView();
            }
        }
        else
        {
            _hudCanvas.Visible = true;
            _sessionMenu.HideMenu();
        }

        if (minimapDemo && _scenario == "ustye-raid" && _session is LocalSession && _screenshotPath is not null)
        {
            SetupMinimapDemo();
        }

        if (_forestDemoCaptureDir is not null && _scenario == "forest-visibility")
        {
            _showVegetationDebug = true;
            _cameraRig.Model.CurrentSize = 130f;
            _cameraRig.Model.SetZoom(130f);
            _cameraRig.CenterOn(new Vector3(95f, 0f, 110f));
            UpdateVegetationDebugOverlay();
            _forestDemoStage = 0;
            _forestDemoTimer = 0.8;
        }

        if (_smokeTest && !ClientRegressionTests.Run(this))
        {
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }

        GD.Print($"M0_CLIENT_READY runtime={System.Environment.Version} own_units={_session.Replica.OwnUnits.Length}");

        bool isPerfProbe = userArgs.Contains("--perf-probe") || allArgs.Contains("--perf-probe");
        if (isPerfProbe)
        {
            SetProcess(false);
            _ = ClientPerfProbe.Run(this, noPrewarm).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"CLIENT_PERF_PROBE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        if (effectsCaptureDir is not null)
        {
            _ = VehicleEffectsProbe.RunCapture(this, effectsCaptureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"VEHICLE_EFFECTS_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        if (captureDir is not null)
        {
            _ = CombatFeedbackProbe.RunVisualCapture(this, captureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"COMBAT_FEEDBACK_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        if (orderPreviewCaptureDir is not null)
        {
            _ = OrderPreviewProbe.RunCapture(this, orderPreviewCaptureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"ORDER_PREVIEW_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void BuildEnvironment(StaticGeometry? scenarioGeometry)
    {
        ClearEnvironment();

        if (_scenario == "ustye-ground" || _scenario == "ustye-raid")
        {
            UstyePlanData plan = UstyePlanData.Load();
            _ustyeMapView = new(plan, includeScaleVehicles: false, fogOverlay: _fogOfWar);
            _environmentRoot.AddChild(_ustyeMapView);
            _terrainWindingValid = !_smokeTest || _ustyeMapView.HasUpwardFrontFaces();
        }
        else
        {
            MeshInstance3D terrain = TerrainView.Create(_map, _fogOfWar);
            _environmentRoot.AddChild(terrain);
            _terrainWindingValid = !_smokeTest || TerrainView.HasUpwardFrontFaces(terrain);
            _environmentRoot.AddChild(TerrainView.CreateGrid(_map));

            if (scenarioGeometry is not null)
            {
                SetupStructures(scenarioGeometry);
            }
        }
    }

    private void ClearEnvironment()
    {
        foreach (Node child in _environmentRoot.GetChildren())
        {
            _environmentRoot.RemoveChild(child);
            child.QueueFree();
        }
        _ustyeMapView = null;
    }

    private void SetupStructures(StaticGeometry geometry)
    {
        StandardMaterial3D buildingMat = new() { AlbedoColor = new Color("5c5346"), Roughness = 0.85f };
        StandardMaterial3D wallMat = new() { AlbedoColor = new Color("6b756c"), Roughness = 0.9f };

        foreach (OpaqueStructure structure in geometry.Structures)
        {
            float sizeX = structure.Max.X - structure.Min.X;
            float sizeY = structure.Max.Y - structure.Min.Y;
            float sizeZ = structure.Max.Z - structure.Min.Z;
            float posX = (structure.Min.X + structure.Max.X) * 0.5f;
            float posY = (structure.Min.Y + structure.Max.Y) * 0.5f;
            float posZ = (structure.Min.Z + structure.Max.Z) * 0.5f;

            _environmentRoot.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(sizeX, sizeY, sizeZ) },
                Position = new Vector3(posX, posY, posZ),
                MaterialOverride = structure.Id.Value == 1 ? buildingMat : wallMat
            });
        }

        StandardMaterial3D bridgeMat = new() { AlbedoColor = new Color("454c4d"), Roughness = 0.85f };
        foreach (BridgeStructure bridge in geometry.Bridges)
        {
            System.Numerics.Vector2 delta = bridge.End - bridge.Start;
            float length = delta.Length();
            System.Numerics.Vector2 mid = (bridge.Start + bridge.End) * 0.5f;
            float midHeight = (bridge.StartHeight + bridge.EndHeight) * 0.5f - bridge.Thickness * 0.5f;
            float yaw = MathF.Atan2(delta.X, delta.Y);

            _environmentRoot.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(bridge.Width, bridge.Thickness, length) },
                Position = new Vector3(mid.X, midHeight, mid.Y),
                Rotation = new Vector3(0, yaw, 0),
                MaterialOverride = bridgeMat
            });
        }

        StandardMaterial3D forestMat = new() { AlbedoColor = new Color(0.12f, 0.35f, 0.15f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        foreach (TerrainObstacle obs in geometry.TerrainObstacles)
        {
            if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
            {
                float sizeX = obs.MaxX - obs.MinX;
                float sizeZ = obs.MaxZ - obs.MinZ;
                float posX = (obs.MinX + obs.MaxX) * 0.5f;
                float posZ = (obs.MinZ + obs.MaxZ) * 0.5f;
                float height = 4.0f;
                float groundY = geometry.HeightMap.GetHeight(posX, posZ);
                _environmentRoot.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(sizeX, height, sizeZ) },
                    Position = new Vector3(posX, groundY + height * 0.5f, posZ),
                    MaterialOverride = forestMat
                });
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_sessionMenu?.IsOpen == true)
        {
            if (_lifecycleController.IsConnecting)
            {
                _lifecycleController.Advance(delta);
                _sessionMenu.SetStatus(_lifecycleController.StatusDescription, _lifecycleController.State);
            }
            _cameraRig.Update(delta, allowInput: false);
            ProcessScreenshot(delta);
            ProcessForestDemoCapture(delta);
            return;
        }

        if (_session is null) return;

        long frameStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float wallClockIntervalMs = 0f;
        if (_lastFrameTimestamp > 0)
        {
            wallClockIntervalMs = (float)((frameStartTimestamp - _lastFrameTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        _lastFrameTimestamp = frameStartTimestamp;

        if (_lifecycleController.NetworkSession is not null && ReferenceEquals(_lifecycleController.NetworkSession, _session))
        {
            _lifecycleController.Advance(delta);
            if (_lifecycleController.State == ClientLifecycleState.Disconnected)
            {
                return;
            }
        }
        else
        {
            _session.Advance(delta);
        }
        _fogOfWar?.Update((float)delta, _session.Replica, _catalog);
        UpdateViews();
        _unitViews.FaceHealthBars(_camera.GlobalTransform.Basis);
        _unitViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateEffects(delta, _cameraRig.Focus, _cameraRig.Size, _session.Tick);
        _unitViews.UpdateRecoil(delta);
        _unitViews.UpdateEffects(delta);
        _combatEffects.Update(delta);
        _combatEffects.ProcessEvents(_session.Replica.Events, _unitViews, _session.Tick, _contactViews);
        _wreckViews.ProcessEvents(_session.Replica.Events, _session.Tick);
        _combatEffects.UpdateListener(_cameraRig.Focus);
        _missionPanel?.UpdateMission(_session.Mission, _session.Tick);

        if (!string.Equals(_subtitle.Text, _session.StatusDescription, StringComparison.Ordinal))
        {
            _subtitle.Text = _session.StatusDescription;
        }
        if (_title is not null && (_session.AssignedPlayerId != _lastTitlePlayerId || _scenario != _lastTitleScenario))
        {
            _lastTitlePlayerId = _session.AssignedPlayerId;
            _lastTitleScenario = _scenario;
            _title.Text = FormatTitle(_session.AssignedPlayerId, _scenario);
        }

        if (_session.AssignedPlayerId == 0)
        {
            _homeViewAssignedPlayerId = 0;
        }
        else if (_session.AssignedPlayerId != _homeViewAssignedPlayerId)
        {
            _homeViewAssignedPlayerId = _session.AssignedPlayerId;
            ApplyPlayerHomeView(_session.AssignedPlayerId);
        }

        _orderNotification?.Update(delta);
        UpdateSelectionPresentation();
        _previewController?.UpdateOrders(_session, delta);

        if (!_settingsPanel.IsOpen && !_pauseMenu.IsOpen)
        {
            Vector2 mousePos = GetViewport().GetMousePosition();
            Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
            bool isOccluded = false;
            for (int i = 0; i < _inputOccludingPanels.Length; i++)
            {
                if (_inputOccludingPanels[i].HasPoint(mousePos))
                {
                    isOccluded = true;
                    break;
                }
            }
            bool hasEnemyContact = TryGetTargetContactUnderMouse(mousePos, out _);
            _previewController?.UpdateHover(_camera, mousePos, viewportSize, isOccluded, hasEnemyContact, _unitViews, _session.Replica);
        }
        else
        {
            _orderCursorOverlay?.HideTooltip();
        }

        _cameraRig.Update(delta, allowInput: !_settingsPanel.IsOpen && !_pauseMenu.IsOpen);

        if (_roleBadgeOverlay is not null && _topHud is not null && _bottomHud is not null && _selectionCard is not null)
        {
            RefreshOccludingPanels();
            _roleBadgeOverlay.UpdateBadges(_camera, _camera.Size, _unitViews, _contactViews, _session.Replica, _session.Tick, _badgeOccludingPanels);
        }

        _minimap?.UpdateMinimap(delta);

        long frameEndTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float cpuWorkMs = (float)((frameEndTimestamp - frameStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        if (_benchmarkMetrics is not null)
        {
            if (_benchmarkWarmupFrames > 0)
            {
                _benchmarkWarmupFrames--;
            }
            else if (_benchmarkMetrics.Count < _benchmarkTargetFrames && wallClockIntervalMs > 0f)
            {
                _benchmarkMetrics.Record(wallClockIntervalMs, cpuWorkMs);

                if (_benchmarkMetrics.Count >= _benchmarkTargetFrames)
                {
                    string hardware = $"{OS.GetName()} / {OS.GetProcessorName()} / Cores: {OS.GetProcessorCount()}";
                    Vector2 viewSize = GetViewport().GetVisibleRect().Size;
                    string resolution = $"{viewSize.X:F0}x{viewSize.Y:F0}";
                    string report = _benchmarkMetrics.FormatReport(
                        _scenario, hardware, resolution, _settingsPanel.CurrentSettings.VSyncEnabled, _settingsPanel.CurrentSettings.MaxFps, warmupFrames: 5);
                    GD.Print(report);
                }
            }
        }

        _hudTimer += delta;
        if (_hudTimer >= 0.5)
        {
            double rate = (_session.BytesReceived - _lastBytes) / _hudTimer / 1024;
            _lastBytes = _session.BytesReceived;
            string simInfo = _session is LocalSession local
                ? $"{local.LastTickMilliseconds:F3} ms per tick / {local.LastTickAllocatedBytes} B tick allocations"
                : _session.StatusDescription;
            _stats.Text = $"20 Hz SIM / 10 Hz STATE / TICK {_session.Tick:00000} / OWN: {_session.Replica.OwnUnits.Length} / CONTACTS: {_session.Replica.ContactCount}\n{simInfo} / {rate:F1} KiB/s";
            _hudTimer = 0;
        }

        ProcessScreenshot(delta);
        ProcessForestDemoCapture(delta);

        if (_screenshotOnFinishPath is not null && _missionPanel?.IsMissionFinished == true)
        {
            _screenshotOnFinishTimer -= delta;
            if (_screenshotOnFinishTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    img.SavePng(_screenshotOnFinishPath);
                    GD.Print($"SCREENSHOT_SAVED {_screenshotOnFinishPath}");
                }
                _screenshotOnFinishPath = null;
                if (_screenshotQuit)
                {
                    GetTree().Quit(0);
                }
            }
        }

        if (_smokeTest)
        {
            // In headless smoke tests with the Dummy audio driver, active playbacks stopped during Clear()
            // require a few frames of main loop processing to retire from AudioServer before quitting.
            // Quitting immediately leaves 4 ObjectDB instances (AudioStreamWAV and AudioStreamPlaybackWAV)
            // leaked at exit.
            if (_smokeQuitFrames >= 0)
            {
                _smokeQuitFrames++;
                if (_smokeQuitFrames >= 12)
                {
                    GetTree().Quit(_smokeExitCode);
                }
                return;
            }

            _smokeTimer += delta;

            if (_scenario == "vehicle-traffic")
            {
                var ownUnits = _session.Replica.OwnUnits;
                for (int u = 0; u < ownUnits.Length; u++)
                {
                    if (ownUnits[u].NavStatus == PathRequestStatus.WaitingForClearance)
                    {
                        _trafficObservedWaiting = true;
                    }
                }

                if (_trafficObservedWaiting && !_trafficLeaderCleared && ownUnits.Length > 0)
                {
                    // Move the parked leader (unit 0 at 60,60) North to (60, 100) to free the path for the follower immediately
                    _session.Move(ownUnits[0].Id, new NumericsVector2(60f, 100f));
                    _trafficLeaderCleared = true;
                }

                if (_trafficLeaderCleared && ownUnits.Length > 1)
                {
                    if (ownUnits[1].NavStatus == PathRequestStatus.PathFound)
                    {
                        _trafficObservedResumed = true;
                    }
                }
            }

            float smokeTimeout = _scenario == "vehicle-traffic" ? 5.0f : 1.0f;
            if (_smokeTimer >= smokeTimeout)
            {
                bool ok = _scenario switch
                {
                    "visibility" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 2,
                    "navigation" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "navigation-large" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "combat" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4,
                    "recon-raid" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "battle" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == BattleScenario.UnitsPerSide,
                    "bridge-crossing" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "vehicle-traffic" => _terrainWindingValid && _session.Tick >= 60 && _trafficObservedWaiting && _trafficObservedResumed && _session.Replica.OwnUnits.Length >= 6,
                    "group-move" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length >= 10,
                    "ustye-ground" => (_terrainWindingValid || (_ustyeMapView?.HasUpwardFrontFaces() ?? false)) && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 6,
                    "ustye-raid" => (_terrainWindingValid || (_ustyeMapView?.HasUpwardFrontFaces() ?? false)) && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "forest-visibility" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 5 && CheckForestVisibilityCleanPositions(_session.Replica.OwnUnits),
                    "infantry-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4 && CheckInfantryPlaygroundCleanPositions(_session.Replica.OwnUnits),
                    _ => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 12 &&
                        _session.Replica.OwnUnits[0].Position.Y > 24
                };
                string prefix = _scenario switch
                {
                    "combat" => "M1_CLIENT_COMBAT_SMOKE",
                    "navigation-large" => "M1_CLIENT_NAV_LARGE_SMOKE",
                    "recon-raid" => "M1_CLIENT_RECON_RAID_SMOKE",
                    "battle" => "M1_CLIENT_BATTLE_SMOKE",
                    "bridge-crossing" => "M1_CLIENT_BRIDGE_CROSSING_SMOKE",
                    "vehicle-traffic" => "M1_CLIENT_VEHICLE_TRAFFIC_SMOKE",
                    "group-move" => "M1_CLIENT_GROUP_MOVE_SMOKE",
                    "ustye-ground" => "USTYE_GROUND_SMOKE",
                    "ustye-raid" => "USTYE_RAID_SMOKE",
                    "forest-visibility" => "FOREST_VISIBILITY_SMOKE",
                    "infantry-playground" => "INFANTRY_PLAYGROUND_SMOKE",
                    _ => "M0_CLIENT_SMOKE"
                };
                string token = prefix + (ok ? "_OK" : "_FAILED");
                GD.Print(token);
                _combatEffects?.Clear();
                _smokeQuitFrames = 0;
                _smokeExitCode = ok ? 0 : 1;
            }
        }
    }

    private static bool CheckForestVisibilityCleanPositions(ReadOnlySpan<OwnUnitObservation> ownUnits)
    {
        if (ownUnits.Length != 5) return false;
        NumericsVector2[] expectedPositions =
        [
            ForestVisibilityScenario.ObserverGroundStart,
            ForestVisibilityScenario.ObserverRidgeStart,
            ForestVisibilityScenario.ObserverBeltsStart,
            ForestVisibilityScenario.ObserverSparseStart,
            ForestVisibilityScenario.ObserverOpenStart,
        ];
        for (int i = 0; i < ownUnits.Length; i++)
        {
            if (ownUnits[i].NavStatus != PathRequestStatus.None) return false;
            if ((ownUnits[i].Position - expectedPositions[i]).LengthSquared() > 0.01f) return false;
        }
        return true;
    }

    private static bool CheckInfantryPlaygroundCleanPositions(ReadOnlySpan<OwnUnitObservation> ownUnits)
    {
        if (ownUnits.Length != 4) return false;
        NumericsVector2[] expectedPositions =
        [
            InfantryPlaygroundScenario.UsRiflemanOpen,
            InfantryPlaygroundScenario.UsRiflemanForest,
            InfantryPlaygroundScenario.UsRiflemanBridge,
            InfantryPlaygroundScenario.UsRiflemanBuilding,
        ];
        for (int i = 0; i < ownUnits.Length; i++)
        {
            if (ownUnits[i].NavStatus != PathRequestStatus.None) return false;
            if ((ownUnits[i].Position - expectedPositions[i]).LengthSquared() > 0.01f) return false;
        }
        return true;
    }

    private void ProcessScreenshot(double delta)
    {
        if (_screenshotPath is not null)
        {
            _screenshotTimer -= delta;
            if (_screenshotTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    img.SavePng(_screenshotPath);
                    GD.Print($"SCREENSHOT_SAVED {_screenshotPath}");
                }
                _screenshotPath = null;
                if (_screenshotQuit)
                {
                    GetTree().Quit(0);
                }
            }
        }
    }

    private void ProcessForestDemoCapture(double delta)
    {
        if (_forestDemoCaptureDir is null || _forestDemoStage < 0)
            return;

        _forestDemoTimer -= delta;

        if (_forestDemoStage == 0)
        {
            if (_forestDemoTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    string path1 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_1_before_detection.png");
                    img.SavePng(path1);
                    GD.Print($"FOREST_DEMO_SCREENSHOT 1: {path1}");
                }

                if (_session is LocalSession local)
                {
                    local.Move(new EntityId(1), new NumericsVector2(115f, 110f));
                }
                _forestDemoStage = 1;
                _forestDemoTimer = 0.0;
            }
        }
        else if (_forestDemoStage == 1)
        {
            UpdateVegetationDebugOverlay();
            bool spotted = false;
            foreach (ContactObservation c in _session.Replica.Contacts)
            {
                if (c.State == ContactState.Current && NumericsVector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 5.0f)
                {
                    spotted = true;
                    break;
                }
            }

            if (spotted)
            {
                if (!_forestDemoTargetSpotted)
                {
                    _forestDemoTargetSpotted = true;
                    _forestDemoTimer = 0.5;
                }
                else if (_forestDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string path2 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_2_after_detection.png");
                        img.SavePng(path2);
                        GD.Print($"FOREST_DEMO_SCREENSHOT 2: {path2}");
                    }

                    if (_session is LocalSession local)
                    {
                        local.Move(new EntityId(1), new NumericsVector2(60f, 110f));
                    }
                    _forestDemoStage = 2;
                    _forestDemoTimer = 0.0;
                }
            }
        }
        else if (_forestDemoStage == 2)
        {
            UpdateVegetationDebugOverlay();
            bool transitioned = false;
            foreach (ContactObservation c in _session.Replica.Contacts)
            {
                if (c.State == ContactState.LastKnown && NumericsVector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 5.0f)
                {
                    transitioned = true;
                    break;
                }
            }

            if (transitioned)
            {
                if (!_forestDemoContactLost)
                {
                    _forestDemoContactLost = true;
                    _forestDemoTimer = 0.6;
                }
                else if (_forestDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string path3 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_3_after_contact_loss.png");
                        img.SavePng(path3);
                        GD.Print($"FOREST_DEMO_SCREENSHOT 3: {path3}");
                    }
                    _forestDemoStage = -1;
                    GetTree().Quit(0);
                }
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_settingsPanel?.IsOpen == true || _sessionMenu?.IsOpen == true || _pauseMenu?.IsOpen == true)
        {
            CancelActiveDrag();
            return;
        }

        if (_selectionController.IsPointerDown)
        {
            if (_settingsPanel?.IsOpen == true)
            {
                CancelActiveDrag();
                return;
            }

            if (@event is InputEventMouseMotion motion)
            {
                HandleLeftMouseMotion(motion.Position);
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release)
            {
                HandleLeftMouseRelease(release.Position, release.ShiftPressed);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (_previewController?.IsFacingGestureActive == true)
        {
            if (_settingsPanel?.IsOpen == true)
            {
                CancelActiveDrag();
                return;
            }

            if (@event is InputEventMouseMotion motion)
            {
                _previewController.UpdateFacingGesture(_camera, motion.Position, _unitViews);
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } release)
            {
                HandleRightMouseRelease(release.Position);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_settingsPanel?.IsOpen == true)
        {
            CancelActiveDrag();
            if (@event is InputEventKey { Pressed: true, Echo: false } settingsKey)
            {
                if (settingsKey.Keycode == Key.Escape)
                {
                    _settingsPanel?.Close();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (settingsKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            // Centrally block all gameplay input while settings overlay is open
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_pauseMenu?.IsOpen == true)
        {
            CancelActiveDrag();
            if (@event is InputEventKey { Pressed: true, Echo: false } pauseKey)
            {
                if (pauseKey.Keycode == Key.Escape)
                {
                    _pauseMenu.Close();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (pauseKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_sessionMenu?.IsOpen == true)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false } menuKey)
            {
                if (menuKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (menuKey.Keycode == Key.Escape)
                {
                    if (_sessionMenu.HandleEscape())
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_missionPanel?.IsMissionFinished == true)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && _session is LocalSession)
            {
                OnRetryMission();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                _status.Text = _scenario == "battle" ? "Battle ended. Orders are disabled." : "Mission ended. Orders are disabled.";
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F })
            {
                _status.Text = _scenario == "battle" ? "Battle ended. Orders are disabled." : "Mission ended. Orders are disabled.";
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F7 || ((key.Keycode is Key.F9 or Key.V) && key.ShiftPressed))
            {
                if (_fogOfWar is not null)
                {
                    _fogOfWar.IsCalculationSuspended = !_fogOfWar.IsCalculationSuspended;
                    _status.Text = _fogOfWar.IsCalculationSuspended
                        ? "Diagnostic: Fog of war CPU calculation SUSPENDED"
                        : "Diagnostic: Fog of war CPU calculation RESUMED";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F9 or Key.V)
            {
                _fogOfWar?.Toggle();
                _status.Text = _fogOfWar?.IsEnabled == true ? "Fog of war enabled" : "Fog of war disabled";
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F10 or Key.O)
            {
                CancelActiveDrag();
                _settingsPanel?.Toggle();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F6)
            {
                _showVegetationDebug = !_showVegetationDebug;
                _status.Text = _showVegetationDebug ? "Vegetation overlay: ON" : "Vegetation overlay: OFF";
                UpdateVegetationDebugOverlay();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F8)
            {
                _showVehicleCollisionBounds = !_showVehicleCollisionBounds;
                _status.Text = _showVehicleCollisionBounds ? "Collision bounds overlay: ON" : "Collision bounds overlay: OFF";
                UpdateCollisionDebugOverlay();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_scenario == "ustye-ground" && _session is LocalSession && key.Keycode is Key.F1 or Key.F2 or Key.F3 or Key.F4)
            {
                int newSetup = (int)(key.Keycode - Key.F1);
                SwitchUstyeSetup(newSetup);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.F5 || (key.Keycode == Key.R && (key.CtrlPressed || key.MetaPressed))) && _session is LocalSession)
            {
                if (_scenario == "ustye-ground")
                {
                    SwitchUstyeSetup((_ustyeSetupIndex + 1) % 4);
                }
                else
                {
                    ResetLocalScenario();
                    _status.Text = "Scenario reset to initial state";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode == Key.Escape)
            {
                if (_previewController?.IsFacingGestureActive == true)
                {
                    _previewController.CancelFacingGesture();
                    _status.Text = "Move order cancelled";
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (_unitViews.Selected.Count > 0 || _contactViews.Selected.Count > 0)
                {
                    CancelActiveDrag();
                    _unitViews.Selected.Clear();
                    _contactViews.Selected.Clear();
                    _unitViews.UpdateSelectionVisuals();
                    _status.Text = "No units selected";
                    GetViewport().SetInputAsHandled();
                    return;
                }

                CancelActiveDrag();
                _pauseMenu?.Open();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode == Key.Space)
            {
                _cameraRig.ResetToDefault();
                return;
            }

            if (_scenario == "navigation-large" && _unitViews.Selected.Count > 0 &&
                TryGetNavigationLargeDebugTarget(key.Keycode, key.AltPressed, key.CtrlPressed || key.MetaPressed, key.ShiftPressed, out NumericsVector2 testTarget))
            {
                foreach (EntityId id in _unitViews.Selected)
                {
                    _session.Move(id, testTarget);
                }
                _status.Text = $"Route target set to {testTarget}";
                return;
            }

            GroupKeyAction groupAction = SelectionController.ClassifyGroupKey(
                key.Keycode, key.CtrlPressed, key.MetaPressed, key.ShiftPressed, key.AltPressed, out int groupNumber);
            if (groupAction != GroupKeyAction.None)
            {
                switch (groupAction)
                {
                    case GroupKeyAction.Store:
                        _selectionController.StoreControlGroup(_unitViews, groupNumber);
                        UpdateSelectionPresentation();
                        break;
                    case GroupKeyAction.Add:
                        _selectionController.AddToControlGroup(_unitViews, groupNumber);
                        UpdateSelectionPresentation();
                        _previewController?.OnUnitSelectionChanged();
                        break;
                    case GroupKeyAction.Recall:
                        double nowSeconds = Time.GetTicksUsec() / 1_000_000.0;
                        if (_selectionController.RecallControlGroup(_unitViews, _contactViews, _cameraRig, groupNumber, nowSeconds, out _))
                        {
                            _unitViews.UpdateSelectionVisuals();
                            UpdateSelectionPresentation();
                            _previewController?.OnUnitSelectionChanged();
                        }
                        break;
                }
                return;
            }

            if (key.Keycode == Key.F && _unitViews.Selected.Count > 0 && TryGetFirstSelectedContact(out ContactId target))
            {
                int totalUnits = _unitViews.Selected.Count;
                int sent = 0;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.Attack(id, target)) sent++;
                }
                string attackMsg = sent == 0
                    ? $"Failed to submit attack order / 0 of {totalUnits} units -> Contact #{target.Value}"
                    : sent < totalUnits
                        ? $"Attack order submitted for {sent} of {totalUnits} units -> Contact #{target.Value}"
                        : $"Attack order submitted / {sent} units -> Contact #{target.Value}";
                _orderNotification?.ShowNotification(attackMsg);
                _status.Text = attackMsg;
            }

            if (key.Keycode is Key.Z or Key.X or Key.C && _unitViews.Selected.Count > 0)
            {
                FireStance stance = key.Keycode switch
                {
                    Key.Z => FireStance.FireAtWill,
                    Key.X => FireStance.ReturnFire,
                    Key.C => FireStance.HoldFire,
                    _ => FireStance.FireAtWill
                };
                string stanceLabel = stance switch
                {
                    FireStance.FireAtWill => "Fire at will",
                    FireStance.ReturnFire => "Return fire",
                    FireStance.HoldFire => "Hold fire",
                    _ => "Fire at will"
                };

                int totalUnits = _unitViews.Selected.Count;
                int sent = 0;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.SetFireStance(id, stance)) sent++;
                }

                string stanceMsg = sent == 1
                    ? $"Fire stance set to {stanceLabel} for 1 unit"
                    : $"Fire stance set to {stanceLabel} for {sent} units";
                _orderNotification?.ShowNotification(stanceMsg);
                _status.Text = stanceMsg;
                UpdateSelectionPresentation();
                GetViewport().SetInputAsHandled();
                return;
            }
            return;
        }

        if (_cameraRig.HandleZoomInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventMouseButton mouse) return;

        if (mouse.ButtonIndex == MouseButton.Left && !mouse.Pressed)
        {
            if (_selectionController.IsPointerDown)
            {
                HandleLeftMouseRelease(mouse.Position, mouse.ShiftPressed);
            }
            return;
        }

        if (mouse.ButtonIndex == MouseButton.Right && !mouse.Pressed)
        {
            if (_previewController?.IsFacingGestureActive == true)
            {
                HandleRightMouseRelease(mouse.Position);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (!mouse.Pressed) return;

        if (mouse.ButtonIndex == MouseButton.Left)
        {
            RefreshOccludingPanels();
            if (_selectionController.BeginPress(mouse.Position, mouse.ShiftPressed, mouse.DoubleClick, _inputOccludingPanels))
            {
                _selectionBoxOverlay?.HideBox();
            }
            return;
        }

        if (mouse.ButtonIndex == MouseButton.Right)
        {
            if (mouse.AltPressed) return;
            RefreshOccludingPanels();
            if (SelectionController.IsPointOccluded(mouse.Position, _inputOccludingPanels))
                return;

            if (_unitViews.Selected.Count > 0)
            {
                if (TryGetTargetContactUnderMouse(mouse.Position, out ContactId targetContact))
                {
                    int totalUnits = _unitViews.Selected.Count;
                    int sent = 0;
                    foreach (EntityId id in _unitViews.Selected)
                    {
                        if (_session.Attack(id, targetContact)) sent++;
                    }
                    _destination.Clear();
                    string attackMsg = sent == 0
                        ? $"Failed to submit attack order / 0 of {totalUnits} units -> Contact #{targetContact.Value}"
                        : sent < totalUnits
                            ? $"Attack order submitted for {sent} of {totalUnits} units -> Contact #{targetContact.Value}"
                            : $"Attack order submitted / {sent} units -> Contact #{targetContact.Value}";
                    _orderNotification?.ShowNotification(attackMsg);
                    _status.Text = attackMsg;
                    return;
                }

                bool isReverse = Input.IsKeyPressed(Key.R);
                MoveMode moveMode = isReverse ? MoveMode.Reverse : MoveMode.Forward;
                if (_previewController.BeginFacingGesture(_camera, mouse.Position, _unitViews, _session, out string failMsg, moveMode))
                {
                    GetViewport().SetInputAsHandled();
                }
                else if (!string.IsNullOrEmpty(failMsg))
                {
                    _status.Text = failMsg;
                }
            }
            else if (_contactViews.Selected.Count > 0)
            {
                _orderNotification?.ShowNotification("Enemy contacts cannot receive orders");
                _status.Text = "Enemy contacts cannot receive orders";
            }
        }
    }

    private bool TryGetTargetContactUnderMouse(Vector2 mousePos, out ContactId targetContact)
    {
        targetContact = default;
        if (_roleBadgeOverlay is not null &&
            _roleBadgeOverlay.TryHitBadge(mousePos, out _, out ContactId? badgeEnemy) &&
            badgeEnemy is not null)
        {
            targetContact = badgeEnemy.Value;
            return true;
        }

        if (_contactViews is null) return false;

        float nearestContactDistance = 28 * 28;
        bool found = false;
        foreach ((ContactId id, ContactView view) in _contactViews.Views)
        {
            Vector3 center = view.Root.Position + Vector3.Up * 1.5f;
            if (_camera.IsPositionBehind(center)) continue;
            float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mousePos);
            if (distance < nearestContactDistance)
            {
                nearestContactDistance = distance;
                targetContact = id;
                found = true;
            }
        }
        return found;
    }

    private void HandleLeftMouseMotion(Vector2 position)
    {
        if (_selectionController.UpdateDrag(position))
        {
            _selectionBoxOverlay?.ShowBox(_selectionController.CurrentBoxRect);
        }
        else
        {
            _selectionBoxOverlay?.HideBox();
        }
    }

    private void HandleLeftMouseRelease(Vector2 position, bool shiftPressed)
    {
        _selectionBoxOverlay?.HideBox();
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        SelectionReleaseKind kind = _selectionController.CompleteRelease(
            _camera,
            _unitViews,
            _contactViews,
            _roleBadgeOverlay,
            position,
            viewportSize,
            shiftPressed,
            out _,
            out _);

        if (kind is SelectionReleaseKind.BoxSelect or SelectionReleaseKind.ClickSelect or SelectionReleaseKind.DoubleClickSelect)
        {
            _unitViews.UpdateSelectionVisuals();
            UpdateSelectionPresentation();
            _previewController?.OnUnitSelectionChanged();
        }
    }

    private void HandleRightMouseRelease(Vector2 position)
    {
        if (_previewController.EndFacingGesture(_camera, position, _unitViews, _session, out string moveMsg))
        {
            _status.Text = moveMsg;
        }
        else if (!string.IsNullOrEmpty(moveMsg))
        {
            _status.Text = moveMsg;
        }
    }

    internal void CancelActiveDrag()
    {
        _selectionController.CancelDrag();
        _selectionBoxOverlay?.HideBox();
        _previewController?.CancelFacingGesture();
        _minimap?.CancelDrag();
    }

    internal void RefreshOccludingPanels()
    {
        if (_topHud is null || _bottomHud is null || _selectionCard is null)
            return;

        Rect2 topRect = _topHud.GetGlobalRect();
        Rect2 bottomRect = _bottomHud.GetGlobalRect();
        Rect2 cardRect = _selectionCard.GetGlobalRect();
        Rect2 settingsRect = _settingsPanel.IsOpen ? _settingsPanel.GetContainerRect() : default;
        Rect2 missionRect = _missionPanel is not null ? (_missionPanel.IsMissionFinished ? _missionPanel.GetResultRect() : _missionPanel.GetBriefingRect()) : default;
        Rect2 minimapRect = _minimap is not null ? _minimap.GetGlobalRect() : default;

        _inputOccludingPanels[0] = topRect;
        _inputOccludingPanels[1] = bottomRect;
        _inputOccludingPanels[2] = cardRect;
        _inputOccludingPanels[3] = settingsRect;
        _inputOccludingPanels[4] = missionRect;
        _inputOccludingPanels[5] = minimapRect;

        _badgeOccludingPanels[0] = topRect;
        _badgeOccludingPanels[1] = bottomRect;
        _badgeOccludingPanels[2] = cardRect;
        _badgeOccludingPanels[3] = _orderNotification is { Visible: true } ? _orderNotification.GetGlobalRect() : default;
        _badgeOccludingPanels[4] = settingsRect;
        _badgeOccludingPanels[5] = missionRect;
        _badgeOccludingPanels[6] = minimapRect;
    }

    internal static bool TryGetNavigationLargeDebugTarget(
        Key keycode,
        bool altPressed,
        bool ctrlOrCmdPressed,
        bool shiftPressed,
        out NumericsVector2 target)
    {
        if (!altPressed || ctrlOrCmdPressed || shiftPressed)
        {
            target = default;
            return false;
        }

        target = keycode switch
        {
            Key.Key1 => new NumericsVector2(300, 480),
            Key.Key2 => new NumericsVector2(450, 480),
            Key.Key3 => new NumericsVector2(150, 320),
            Key.Key4 => new NumericsVector2(500, 150),
            Key.Key5 => new NumericsVector2(560, 560),
            Key.Key6 => new NumericsVector2(80, 520),
            _ => default
        };
        return target.X > 0f;
    }

    private static readonly string[] ControlGroupSuffixes =
    [
        string.Empty,
        " (Group 1)",
        " (Group 2)",
        " (Group 3)",
        " (Group 4)",
        " (Group 5)",
        " (Group 6)",
        " (Group 7)",
        " (Group 8)",
        " (Group 9)"
    ];

    internal static string GetControlGroupSuffix(int groupNumber) =>
        groupNumber >= 1 && groupNumber <= 9 ? ControlGroupSuffixes[groupNumber] : string.Empty;

    private int _lastTitlePlayerId = -1;
    private string? _lastTitleScenario;

    private enum SelectionPresentationKind { None, SingleUnit, MultipleUnits, Contact }
    private SelectionPresentationKind _presentedKind = SelectionPresentationKind.None;
    private EntityId _presentedSingleUnitId;
    private int _presentedActiveGroup = -1;
    private uint _presentedMultiHash;
    private int _presentedMultiCount = -1;
    private ContactId _presentedContactId;
    private string _cachedStatusText = string.Empty;

    internal void UpdateSelectionPresentation()
    {
        int activeGroup = _unitViews.GetSelectedControlGroup();

        if (_unitViews.Selected.Count == 1 && TryGetFirstSelectedUnit(out EntityId selId))
        {
            if (_session.Replica.TryGetOwnUnit(selId, out OwnUnitObservation ownUnit))
            {
                _selectionCard?.UpdateSingleOwnUnit(ownUnit, activeGroup);
                if (_presentedKind != SelectionPresentationKind.SingleUnit ||
                    _presentedSingleUnitId != selId ||
                    _presentedActiveGroup != activeGroup)
                {
                    _presentedKind = SelectionPresentationKind.SingleUnit;
                    _presentedSingleUnitId = selId;
                    _presentedActiveGroup = activeGroup;
                    string groupSuffix = GetControlGroupSuffix(activeGroup);
                    _cachedStatusText = $"1 unit selected{groupSuffix} / Unit #{ownUnit.Id.Value} / RMB terrain to move, RMB enemy to attack";
                    _status.Text = _cachedStatusText;
                }
            }
        }
        else if (_unitViews.Selected.Count > 1)
        {
            uint hash = ComputeSelectionHash(_unitViews.Selected);
            if (_presentedKind != SelectionPresentationKind.MultipleUnits ||
                _presentedMultiCount != _unitViews.Selected.Count ||
                _presentedActiveGroup != activeGroup ||
                _presentedMultiHash != hash)
            {
                _presentedKind = SelectionPresentationKind.MultipleUnits;
                _presentedMultiCount = _unitViews.Selected.Count;
                _presentedActiveGroup = activeGroup;
                _presentedMultiHash = hash;

                int tanks = 0, recons = 0, others = 0;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation u))
                    {
                        if (u.Definition < _catalog.Units.Length)
                        {
                            string cls = _catalog.Units[u.Definition].Classification ?? string.Empty;
                            if (cls.Equals("tank", StringComparison.OrdinalIgnoreCase)) tanks++;
                            else if (cls.Equals("recon", StringComparison.OrdinalIgnoreCase)) recons++;
                            else others++;
                        }
                        else others++;
                    }
                }

                _selectionCard?.UpdateMultipleOwnUnits(_unitViews.Selected.Count, tanks, recons, others, activeGroup);
                string groupSuffix = GetControlGroupSuffix(activeGroup);
                _cachedStatusText = $"{_unitViews.Selected.Count} units selected{groupSuffix} / RMB terrain to move, RMB enemy to attack";
                _status.Text = _cachedStatusText;
            }
        }
        else if (_contactViews.Selected.Count > 0 && TryGetFirstSelectedContact(out ContactId firstSelected))
        {
            if (_contactViews.Views.TryGetValue(firstSelected, out ContactView? cv))
            {
                _selectionCard?.UpdateContact(cv.Observation, _session.Tick);
                if (_presentedKind != SelectionPresentationKind.Contact ||
                    _presentedContactId != firstSelected)
                {
                    _presentedKind = SelectionPresentationKind.Contact;
                    _presentedContactId = firstSelected;
                    _cachedStatusText = $"Contact #{cv.Observation.Id.Value} selected / Right-click with armed tank to attack";
                    _status.Text = _cachedStatusText;
                }
            }
        }
        else
        {
            _selectionCard?.UpdateEmpty();
            if (_presentedKind != SelectionPresentationKind.None)
            {
                _presentedKind = SelectionPresentationKind.None;
                _cachedStatusText = "No units selected / Left-click unit or contact to inspect";
                _status.Text = _cachedStatusText;
            }
        }
    }

    private static uint ComputeSelectionHash(HashSet<EntityId> set)
    {
        uint hash = 17;
        foreach (EntityId id in set)
        {
            hash = hash * 31 + id.Value;
        }
        return hash;
    }

    private bool TryGetFirstSelectedUnit(out EntityId first)
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            first = id;
            return true;
        }
        first = default;
        return false;
    }

    private bool TryGetFirstSelectedContact(out ContactId first)
    {
        foreach (ContactId id in _contactViews.Selected)
        {
            first = id;
            return true;
        }
        first = default;
        return false;
    }

    internal void UpdateViews()
    {
        int selectedUnits = _unitViews.Selected.Count;
        int selectedContacts = _contactViews.Selected.Count;
        _unitViews.Sync(_session.Replica, _session.RenderTick);
        _contactViews.Sync(_session.Replica, _session.RenderTick);
        _wreckViews.Sync(_session.Replica);
        if (selectedUnits != _unitViews.Selected.Count || selectedContacts != _contactViews.Selected.Count)
        {
            if (_unitViews.Selected.Count > 0)
            {
                int groupNum = _unitViews.GetSelectedControlGroup();
                string groupSuffix = groupNum > 0 ? $" (Group {groupNum})" : string.Empty;
                _status.Text = $"{_unitViews.Selected.Count} selected{groupSuffix} / right-click terrain to move";
            }
            else if (_contactViews.Selected.Count > 0)
                _status.Text = $"{_contactViews.Selected.Count} contacts selected / cannot receive orders";
            else
                _status.Text = "No selection / unit or contact removed from view";
        }
        UpdateCollisionDebugOverlay();
        UpdateVegetationDebugOverlay();
    }

    private void UpdateCollisionDebugOverlay()
    {
        _collisionDebugMesh.ClearSurfaces();
        if (!_showVehicleCollisionBounds || _map is null)
        {
            return;
        }

        const int circleSegments = 36;
        const float angleStep = Mathf.Tau / circleSegments;

        var ownUnits = _session.Replica.OwnUnits;
        if (ownUnits.Length == 0) return;

        int totalCircleVertices = ownUnits.Length * circleSegments * 2;
        if (totalCircleVertices > 0)
        {
            _collisionDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, CollisionBoundsMaterial);
            for (int u = 0; u < ownUnits.Length; u++)
            {
                var unit = ownUnits[u];
                float colRadius = 0f;
                float nominalSpeed = 8.0f;
                if (unit.Definition < _catalog.Units.Length)
                {
                    colRadius = _catalog.Units[unit.Definition].CollisionRadius;
                    if (colRadius <= 0f) colRadius = _catalog.Units[unit.Definition].Radius;
                    nominalSpeed = _catalog.Units[unit.Definition].Speed;
                }
                if (colRadius <= 0f) colRadius = 2.0f;

                Color ringColor;
                if (unit.NavStatus == PathRequestStatus.WaitingForClearance)
                {
                    ringColor = new Color(0.95f, 0.2f, 0.2f, 0.9f); // Red: stopped and waiting for clearance
                }
                else if (unit.NavStatus == PathRequestStatus.PathFound)
                {
                    float dtSeconds = 0.2f;
                    float dtTicks = dtSeconds * Simulation.TickRate;
                    NumericsVector2 pNow = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                    NumericsVector2 pPast = _session.Replica.SamplePosition(unit.Id, MathF.Max(0f, (float)_session.RenderTick - dtTicks));
                    float curSpeed = (pNow - pPast).Length() / dtSeconds;
                    if (curSpeed < nominalSpeed * 0.85f)
                    {
                        ringColor = new Color(0.95f, 0.85f, 0.2f, 0.9f); // Yellow: following distance deceleration / braking
                    }
                    else
                    {
                        ringColor = new Color(0.2f, 0.95f, 0.3f, 0.9f); // Green: free movement
                    }
                }
                else
                {
                    ringColor = new Color(0.2f, 0.95f, 0.3f, 0.9f); // Green: free movement / idle
                }

                _collisionDebugMesh.SurfaceSetColor(ringColor);
                System.Numerics.Vector2 pos = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                for (int s = 0; s < circleSegments; s++)
                {
                    float t0 = s * angleStep;
                    float t1 = (s + 1) * angleStep;
                    float x0 = pos.X + colRadius * Mathf.Cos(t0);
                    float z0 = pos.Y + colRadius * Mathf.Sin(t0);
                    float x1 = pos.X + colRadius * Mathf.Cos(t1);
                    float z1 = pos.Y + colRadius * Mathf.Sin(t1);

                    float y0 = _map.GetHeight(x0, z0) + 0.15f;
                    float y1 = _map.GetHeight(x1, z1) + 0.15f;
                    _collisionDebugMesh.SurfaceAddVertex(new Vector3(x0, y0, z0));
                    _collisionDebugMesh.SurfaceAddVertex(new Vector3(x1, y1, z1));
                }
            }
            _collisionDebugMesh.SurfaceEnd();
        }

        // Velocity vectors
        int movingCount = 0;
        for (int u = 0; u < ownUnits.Length; u++)
        {
            if (ownUnits[u].NavStatus == PathRequestStatus.PathFound) movingCount++;
        }

        if (movingCount > 0)
        {
            _collisionDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, VelocityDebugMaterial);
            for (int u = 0; u < ownUnits.Length; u++)
            {
                var unit = ownUnits[u];
                if (unit.NavStatus != PathRequestStatus.PathFound) continue;
                System.Numerics.Vector2 pos = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                _session.Replica.SampleYaws(unit.Id, _session.RenderTick, out _, out float hullHeading);
                float arrowLen = 3.0f;
                if (unit.Definition < _catalog.Units.Length)
                {
                    float cr = _catalog.Units[unit.Definition].CollisionRadius;
                    if (cr > 0f) arrowLen = cr * 1.2f;
                }
                NumericsVector2 dir = new(-MathF.Sin(hullHeading), -MathF.Cos(hullHeading));
                NumericsVector2 end = pos + dir * arrowLen;
                float y0 = _map.GetHeight(pos.X, pos.Y) + 0.3f;
                float y1 = _map.GetHeight(end.X, end.Y) + 0.3f;
                _collisionDebugMesh.SurfaceAddVertex(new Vector3(pos.X, y0, pos.Y));
                _collisionDebugMesh.SurfaceAddVertex(new Vector3(end.X, y1, end.Y));
            }
            _collisionDebugMesh.SurfaceEnd();
        }
    }

    private void UpdateVegetationDebugOverlay()
    {
        _vegetationDebugMesh.ClearSurfaces();
        if (!_showVegetationDebug || _scenarioGeometry is null || _map is null)
        {
            return;
        }

        IReadOnlyList<VegetationZone> zones = _scenarioGeometry.VegetationZones;
        if (zones.Count == 0)
        {
            return;
        }

        _vegetationDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, VegetationDebugMaterial);

        // 1. Draw vegetation zone wireframes
        for (int z = 0; z < zones.Count; z++)
        {
            VegetationZone zone = zones[z];
            ReadOnlySpan<System.Numerics.Vector2> vertices = zone.Polygon;
            if (vertices.Length < 3) continue;

            Color zoneColor = zone.Kind switch
            {
                VegetationKind.DenseForest => new Color(0.2f, 0.85f, 0.3f, 0.75f),
                VegetationKind.SparseForest => new Color(0.95f, 0.8f, 0.2f, 0.75f),
                _ => new Color(0.2f, 0.8f, 0.95f, 0.75f)
            };

            for (int i = 0; i < vertices.Length; i++)
            {
                System.Numerics.Vector2 p1 = vertices[i];
                System.Numerics.Vector2 p2 = vertices[(i + 1) % vertices.Length];

                float y1 = _map.GetHeight(p1.X, p1.Y);
                float y2 = _map.GetHeight(p2.X, p2.Y);
                float h = zone.CanopyHeight;

                Vector3 b1 = new(p1.X, y1 + 0.2f, p1.Y);
                Vector3 b2 = new(p2.X, y2 + 0.2f, p2.Y);
                Vector3 t1 = new(p1.X, y1 + h, p1.Y);
                Vector3 t2 = new(p2.X, y2 + h, p2.Y);

                // Ground edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(b1);
                _vegetationDebugMesh.SurfaceAddVertex(b2);

                // Canopy edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(t1);
                _vegetationDebugMesh.SurfaceAddVertex(t2);

                // Vertical corner edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(b1);
                _vegetationDebugMesh.SurfaceAddVertex(t1);
            }
        }

        // 2. Draw observation rays
        ReadOnlySpan<OwnUnitObservation> ownUnits = _session.Replica.OwnUnits;
        if (ownUnits.Length > 0)
        {
            OwnUnitObservation observer = ownUnits[0];
            for (int u = 0; u < ownUnits.Length; u++)
            {
                if (_unitViews.Selected.Contains(ownUnits[u].Id))
                {
                    observer = ownUnits[u];
                    break;
                }
            }

            float obsGround = _map.GetHeight(observer.Position.X, observer.Position.Y);
            System.Numerics.Vector3 eye = new(observer.Position.X, obsGround + 2.0f, observer.Position.Y);

            Span<System.Numerics.Vector2> targets = stackalloc System.Numerics.Vector2[5];
            int targetCount = 0;

            if (_scenario == "forest-visibility")
            {
                targets[0] = ForestVisibilityScenario.TargetEdgePos;
                targets[1] = ForestVisibilityScenario.TargetClearingPos;
                targets[2] = ForestVisibilityScenario.TargetBehindBeltsPos;
                targets[3] = ForestVisibilityScenario.TargetSparsePos;
                targets[4] = ForestVisibilityScenario.TargetOpenPos;
                targetCount = 5;
            }
            else if (_scenario == "infantry-playground")
            {
                targets[0] = InfantryPlaygroundScenario.OpforRiflemanClearing;
                targets[1] = InfantryPlaygroundScenario.OpforRiflemanBridge;
                targets[2] = InfantryPlaygroundScenario.OpforRiflemanBuilding;
                targets[3] = InfantryPlaygroundScenario.OpforVehiclePos;
                targetCount = 4;
            }

            for (int t = 0; t < targetCount; t++)
            {
                System.Numerics.Vector2 targetPos = targets[t];
                float tgtGround = _map.GetHeight(targetPos.X, targetPos.Y);
                System.Numerics.Vector3 targetEye = new(targetPos.X, tgtGround + 2.0f, targetPos.Y);

                LineOfSightResult los = _scenarioGeometry.CheckLineOfSight(eye, targetEye);

                Vector3 startGodot = new(eye.X, eye.Y, eye.Z);
                Vector3 endGodot = new(targetEye.X, targetEye.Y, targetEye.Z);

                if (los.IsClear)
                {
                    Color clearColor = new(0.2f, 0.95f, 0.3f, 0.9f);
                    _vegetationDebugMesh.SurfaceSetColor(clearColor);
                    _vegetationDebugMesh.SurfaceAddVertex(startGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(endGodot);
                }
                else
                {
                    Vector3 hitGodot = new(los.HitPoint.X, los.HitPoint.Y, los.HitPoint.Z);

                    Color blockedColor = new(0.95f, 0.2f, 0.2f, 0.9f);
                    _vegetationDebugMesh.SurfaceSetColor(blockedColor);
                    _vegetationDebugMesh.SurfaceAddVertex(startGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(hitGodot);

                    Color shadowColor = new(0.5f, 0.1f, 0.1f, 0.35f);
                    _vegetationDebugMesh.SurfaceSetColor(shadowColor);
                    _vegetationDebugMesh.SurfaceAddVertex(hitGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(endGodot);

                    Color hitColor = los.ObstructionKind == ObstructionKind.Vegetation
                        ? new Color(0.95f, 0.85f, 0.2f, 0.95f)
                        : new Color(0.95f, 0.2f, 0.2f, 0.95f);

                    float mSize = 0.5f;
                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X - mSize, hitGodot.Y, hitGodot.Z));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X + mSize, hitGodot.Y, hitGodot.Z));

                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y - mSize, hitGodot.Z));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y + mSize, hitGodot.Z));

                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y, hitGodot.Z - mSize));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y, hitGodot.Z + mSize));
                }
            }
        }

        _vegetationDebugMesh.SurfaceEnd();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            CancelActiveDrag();
            _cameraRig?.ClearInput();
            _orderCursorOverlay?.HideTooltip();
            _destination?.HideGhost();
            Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
            Godot.Engine.MaxFps = 30;
        }
        else if (what is (int)NotificationApplicationFocusIn or (int)NotificationWMWindowFocusIn)
        {
            if (_settingsPanel is not null)
            {
                SettingsPanel.ApplyToEngine(_settingsPanel.CurrentSettings);
            }
        }
        else if (what == (int)NotificationWMMouseEnter)
        {
            if ((_selectionController.IsPointerDown || _minimap?.IsDraggingCamera == true) && !Input.IsMouseButtonPressed(MouseButton.Left))
            {
                CancelActiveDrag();
            }
        }
    }

    private void OnSettingsButtonPressed()
    {
        CancelActiveDrag();
        _settingsPanel.Toggle();
    }

    private void BuildHud()
    {
        CanvasLayer canvas = new();
        AddChild(canvas);
        _hudCanvas = canvas;

        _roleBadgeOverlay = new RoleBadgeOverlay(_catalog);
        canvas.AddChild(_roleBadgeOverlay);

        _selectionBoxOverlay = new SelectionBoxOverlay();
        canvas.AddChild(_selectionBoxOverlay);

        PanelContainer top = new() { Position = new Vector2(24, 24), MouseFilter = Control.MouseFilterEnum.Stop };
        top.AddThemeStyleboxOverride("panel", PanelStyle());
        MarginContainer margin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
        VBoxContainer stack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 9);
        _title = new Label
        {
            Text = FormatTitle(_session.AssignedPlayerId, _scenario),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _title.AddThemeFontSizeOverride("font_size", 21);
        _title.AddThemeColorOverride("font_color", new Color("edc76b"));
        stack.AddChild(_title);
        _subtitle = new Label { Text = _session.StatusDescription, MouseFilter = Control.MouseFilterEnum.Ignore };
        _subtitle.AddThemeColorOverride("font_color", new Color("b2beb1"));
        stack.AddChild(_subtitle);
        _stats = new Label { Text = "Starting session...", MouseFilter = Control.MouseFilterEnum.Ignore };
        _stats.AddThemeFontSizeOverride("font_size", 14);
        stack.AddChild(_stats);
        margin.AddChild(stack); top.AddChild(margin); canvas.AddChild(top);
        _topHud = top;

        PanelContainer bottom = new() { MouseFilter = Control.MouseFilterEnum.Stop };
        canvas.AddChild(bottom);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.OffsetLeft = 24; bottom.OffsetRight = -24; bottom.OffsetTop = -88; bottom.OffsetBottom = -24;
        bottom.AddThemeStyleboxOverride("panel", PanelStyle());
        VBoxContainer instructions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

        HBoxContainer footer = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        footer.AddThemeConstantOverride("separation", 16);
        Label controls = new()
        {
            Text = FormatControlsHelpText(OS.GetName() == "macOS"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        controls.AddThemeFontSizeOverride("font_size", 14);
        Button menuBtn = new()
        {
            Text = "MENU [ESC]",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        menuBtn.Pressed += OnMenuButtonPressed;
        Button settingsBtn = new()
        {
            Text = "SETTINGS [F10]",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        settingsBtn.Pressed += OnSettingsButtonPressed;
        footer.AddChild(controls);
        footer.AddChild(menuBtn);
        footer.AddChild(settingsBtn);

        _status = new Label { Text = "  Select a unit to issue orders", MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeColorOverride("font_color", new Color("edc76b"));
        instructions.AddChild(footer); instructions.AddChild(_status); bottom.AddChild(instructions);
        _bottomHud = bottom;

        _selectionCard = new SelectionCard(_catalog);
        _selectionCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        _selectionCard.OffsetLeft = 24;
        _selectionCard.OffsetBottom = -96;
        _selectionCard.OffsetTop = -96 - 100;
        _selectionCard.OffsetRight = 24 + 380;
        canvas.AddChild(_selectionCard);

        _orderNotification = new OrderNotification();
        _orderNotification.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _orderNotification.OffsetBottom = -96;
        _orderNotification.OffsetTop = -96 - 36;
        _orderNotification.OffsetLeft = -180;
        _orderNotification.OffsetRight = 180;
        canvas.AddChild(_orderNotification);

        _orderCursorOverlay = new OrderCursorOverlay();
        canvas.AddChild(_orderCursorOverlay);

        _minimap = new TacticalMinimap();
        _minimap.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _minimap.OffsetRight = -24;
        _minimap.OffsetLeft = -24 - TacticalMinimap.DefaultWidgetSize;
        _minimap.OffsetBottom = -96;
        _minimap.OffsetTop = -96 - TacticalMinimap.DefaultWidgetSize;
        canvas.AddChild(_minimap);

        UstyePlanData? plan = (_scenario == "ustye-ground" || _scenario == "ustye-raid") ? UstyePlanData.Load() : null;
        _minimap.Initialize(_scenarioGeometry!, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, plan);

        if (_scenario == "recon-raid" || _scenario == "ustye-raid" || _scenario == "battle")
        {
            _missionPanel = new MissionPanel(this, _map);
            _missionPanel.SetScenario(_scenario);
            if (_scenario == "ustye-raid")
            {
                _missionPanel.SetBriefing(
                    "OPERATION: USTYE RAID",
                    "Primary: Destroy enemy command vehicle. Preserve recon vehicle.",
                    "Roster: 1x BRDM-2 (3000m sensor), 2x T-62 (115mm gun)");
            }
            else if (_scenario == "battle")
            {
                _missionPanel.SetShowBriefing(false);
            }
            canvas.AddChild(_missionPanel);
            _missionPanel.SetLocalMode(_session is LocalSession);
            _missionPanel.RetryRequested += OnRetryMission;
            _missionPanel.LeaveRequested += LeaveCurrentMatch;
        }
    }

    private void OnRetryMission()
    {
        ResetLocalScenario();
        if (_scenario == "battle")
        {
            _status.Text = "Battle restarted.";
            _orderNotification?.ShowNotification("Battle restarted.");
        }
        else
        {
            _status.Text = "Mission restarted. Objective: Destroy enemy command vehicle. Preserve recon vehicle.";
            _orderNotification?.ShowNotification("Mission restarted. Destroy enemy command vehicle.");
        }
    }

    internal void ResetLocalScenario()
    {
        if (_session is not LocalSession || _scenarioGeometry is null) return;

        CancelActiveDrag();
        _selectionController.Reset();
        _unitViews.Clear();
        _contactViews.Clear();
        _wreckViews.Clear();
        _roleBadgeOverlay?.Clear();
        _selectionCard?.UpdateEmpty();
        _orderNotification?.Clear();
        _destination.Clear();
        _previewController?.Reset();
        _combatEffects.Clear();
        _session.Dispose();

        _presentedKind = SelectionPresentationKind.None;
        _presentedActiveGroup = -1;
        _presentedSingleUnitId = default;
        _presentedMultiCount = -1;
        _presentedMultiHash = 0;
        _presentedContactId = default;

        Simulation newSim = _scenario switch
        {
            "recon-raid" => ReconRaidScenario.CreateSimulation(_scenarioGeometry),
            "bridge-crossing" => BridgeCrossingScenario.CreateSimulation(_scenarioGeometry),
            "vehicle-traffic" => VehicleTrafficScenario.CreateSimulation(_scenarioGeometry),
            "group-move" => GroupMoveScenario.CreateSimulation(_scenarioGeometry),
            "ustye-ground" => UstyeGroundScenario.CreateSimulation(_scenarioGeometry, setupIndex: _ustyeSetupIndex),
            "ustye-raid" => UstyeRaidScenario.CreateSimulation(_scenarioGeometry),
            "battle" => BattleScenario.CreateSimulation(_scenarioGeometry),
            "combat" => CombatScenario.CreateSimulation(_scenarioGeometry),
            "visibility" => VisibilityScenario.CreateSimulation(_scenarioGeometry),
            "navigation" => NavigationScenario.CreateSimulation(_scenarioGeometry),
            "navigation-large" => LargeNavigationScenario.CreateSimulation(_scenarioGeometry),
            _ => PrototypeScenario.Create(_map)
        };
        _session = new LocalSession(newSim);
        _lastCommandResult = 0;

        _cameraRig.ResetToDefault();
        _missionPanel?.ResetUi();
        UstyePlanData? plan = (_scenario == "ustye-ground" || _scenario == "ustye-raid") ? UstyePlanData.Load() : null;
        _minimap?.Initialize(_scenarioGeometry, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, plan);
        _minimap?.Reset();
        UpdateViews();

        if (_session.Replica.OwnUnits.Length > 0)
        {
            _fogOfWar?.UpdateAllNow(_session.Replica, _catalog);
            if (_scenario == "group-move" && _session.Replica.OwnUnits.Length >= 10)
            {
                for (int i = 0; i < 10; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-ground" && _session.Replica.OwnUnits.Length >= 6)
            {
                for (int i = 0; i < 6; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-raid" && _session.Replica.OwnUnits.Length >= 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else
            {
                int selectIdx = _scenario == "vehicle-traffic" && _session.Replica.OwnUnits.Length > 1 ? 1 : 0;
                _unitViews.Selected.Add(_session.Replica.OwnUnits[selectIdx].Id);
            }
        }
    }

    internal void ResetGameplayState()
    {
        CancelActiveDrag();
        _previewController?.CancelFacingGesture();
        _previewController?.Reset();
        _selectionController.Reset();
        _unitViews?.Clear();
        _contactViews?.Clear();
        _wreckViews?.Clear();
        _roleBadgeOverlay?.Clear();
        _selectionCard?.UpdateEmpty();
        _orderNotification?.Clear();
        _destination?.Clear();
        _combatEffects?.Clear();
        _fogOfWar?.Clear();
        _minimap?.Reset();
        _missionPanel?.ResetUi();

        _presentedKind = SelectionPresentationKind.None;
        _presentedActiveGroup = -1;
        _presentedSingleUnitId = default;
        _presentedMultiCount = -1;
        _presentedMultiHash = 0;
        _presentedContactId = default;
        _homeViewAssignedPlayerId = 0;
    }

    private void OnMenuButtonPressed()
    {
        CancelActiveDrag();
        _pauseMenu.Toggle();
    }

    internal void EnsureBattleScenarioContext()
    {
        if (_scenario == "battle" && _scenarioGeometry is not null && _map == _scenarioGeometry.HeightMap && MathF.Abs(_map.Width - BattleScenario.MapSize) < 0.1f)
        {
            return;
        }

        ResetGameplayState();

        _scenario = "battle";
        _scenarioGeometry = BattleScenario.CreateStaticGeometry();
        _map = _scenarioGeometry.HeightMap;
        _matchSetup = BattleScenario.CreateMatchSetup(_scenarioGeometry);
        _catalog = ScenarioContent.LoadCatalog("battle");

        _fogOfWar?.Dispose();
        _fogOfWar = new FogOfWarView(_map, _scenarioGeometry);
        BuildEnvironment(_scenarioGeometry);

        Vector3 initialFocus = new(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y);
        float initialCameraSize = 160f;
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);
        _cameraRig.ResetToDefault();

        _unitViews?.Clear();
        _unitViews = new UnitViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _contactViews?.Clear();
        _contactViews = new ContactViewSet(this, _map, _catalog, () => _matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _wreckViews?.Clear();
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _combatEffects?.Clear();
        _combatEffects?.QueueFree();
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        AddChild(_combatEffects);

        _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);

        if (_missionPanel is not null)
        {
            _missionPanel.SetScenario("battle");
            _missionPanel.ResetUi();
        }

        if (_minimap is not null)
        {
            _minimap.Reset();
            _minimap.Initialize(_scenarioGeometry, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, null);
        }
    }

    internal void SetScenarioForTest(string scenarioName)
    {
        ResetGameplayState();
        _scenario = scenarioName;
        StaticGeometry? scenarioGeometry = null;
        MatchSetup setup;

        if (scenarioName == "prototype")
        {
            _map = HeightMap.CreatePrototype();
            setup = PrototypeScenario.CreateMatchSetup(_map);
            scenarioGeometry = new StaticGeometry(_map, ReadOnlySpan<OpaqueStructure>.Empty);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = PrototypeScenario.LoadCatalog();
            Simulation localSimulation = PrototypeScenario.Create(_map);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "recon-raid")
        {
            scenarioGeometry = ReconRaidScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = ReconRaidScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("recon-raid");
            Simulation localSimulation = ReconRaidScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "battle")
        {
            scenarioGeometry = BattleScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("battle");
            Simulation localSimulation = BattleScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "ustye-raid")
        {
            scenarioGeometry = UstyeRaidScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = UstyeRaidScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("ustye-raid");
            Simulation localSimulation = UstyeRaidScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "forest-visibility")
        {
            scenarioGeometry = ForestVisibilityScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = ForestVisibilityScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("forest-visibility");
            Simulation localSimulation = ForestVisibilityScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "infantry-playground")
        {
            scenarioGeometry = InfantryPlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = InfantryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("infantry-playground");
            Simulation localSimulation = InfantryPlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else
        {
            throw new ArgumentException($"Unsupported test scenario: {scenarioName}");
        }

        _fogOfWar?.Dispose();
        _fogOfWar = new FogOfWarView(_map, scenarioGeometry);
        BuildEnvironment(scenarioGeometry);

        Vector3 initialFocus = scenarioName switch
        {
            "battle" => new Vector3(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y),
            "recon-raid" => new Vector3(100, 0, 100),
            "ustye-raid" => new Vector3(2160f, 10f, 2325f),
            "forest-visibility" => new Vector3(100f, 0f, 110f),
            "infantry-playground" => new Vector3(130f, 0f, 180f),
            _ => new Vector3(64, 0, 64)
        };
        float initialCameraSize = scenarioName switch
        {
            "battle" => 160f,
            "recon-raid" => 160f,
            "ustye-raid" => 160f,
            "forest-visibility" => 150f,
            "infantry-playground" => 120f,
            _ => 112f
        };
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);
        _cameraRig.ResetToDefault();

        _unitViews?.Clear();
        _unitViews = new UnitViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _contactViews?.Clear();
        _contactViews = new ContactViewSet(this, _map, _catalog, () => _matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _wreckViews?.Clear();
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);

        _combatEffects?.Clear();
        _combatEffects?.QueueFree();
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        AddChild(_combatEffects);

        _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);

        EnterGameSession(_session);

        if (scenarioName == "prototype")
        {
            foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
            {
                _session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
            }
            if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
        }
    }

    internal void StartLocalBattle()
    {
        EnsureBattleScenarioContext();
        Simulation localSimulation = BattleScenario.CreateSimulation(_scenarioGeometry!);
        _session = new LocalSession(localSimulation, player: 1);
        EnterGameSession(_session);
    }

    internal void StartMultiplayerConnect(string address)
    {
        EnsureBattleScenarioContext();
        bool ok = _lifecycleController.TryConnect(address, _matchSetup, out string? error);
        if (!ok)
        {
            _sessionMenu.SetStatus(error ?? "Connection failed.", ClientLifecycleState.Failed);
        }
        else
        {
            _sessionMenu.SetStatus(_lifecycleController.StatusDescription, _lifecycleController.State);
        }
    }

    internal void CancelMultiplayerConnect()
    {
        _lifecycleController.Cancel();
        _sessionMenu.SetStatus("Connection cancelled.", ClientLifecycleState.Inactive);
    }

    internal void EnterGameSession(IGameSession session)
    {
        _session = session;
        _isMenuMode = false;
        _sessionMenu.HideMenu();
        _pauseMenu.Close();
        ResetGameplayState();

        _hudCanvas.Visible = true;
        _lastTitlePlayerId = -1;
        _lastTitleScenario = string.Empty;
        _title.Text = FormatTitle(_session.AssignedPlayerId, _scenario);
        _subtitle.Text = _session.StatusDescription;

        if (_missionPanel is not null)
        {
            _missionPanel.SetScenario(_scenario);
            _missionPanel.SetLocalMode(_session is LocalSession);
            _missionPanel.SetShowBriefing(false);
            _missionPanel.ResetUi();
        }

        if (_minimap is not null)
        {
            _minimap.Reset();
            _minimap.Initialize(_scenarioGeometry!, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, null);
        }

        ApplyPlayerHomeView(_session.AssignedPlayerId);
    }

    internal void LeaveCurrentMatch()
    {
        _pauseMenu.Close();
        _lifecycleController.Disconnect();
        if (_session is not null)
        {
            _session.Dispose();
            _session = new NullSession();
        }
        ResetGameplayState();
        _isMenuMode = true;
        _hudCanvas.Visible = false;
        _sessionMenu.ShowMainMenuView();
    }

    private void OnNetworkSessionDisconnected(string reason)
    {
        _pauseMenu.Close();
        if (_session is not null)
        {
            _session.Dispose();
            _session = new NullSession();
        }
        ResetGameplayState();
        _isMenuMode = true;
        _hudCanvas.Visible = false;
        _sessionMenu.ShowConnectionLostModal(reason);
    }

    private void SwitchUstyeSetup(int index)
    {
        _ustyeSetupIndex = Math.Clamp(index, 0, 3);
        ResetLocalScenario();
        Vector3 newFocus = _ustyeSetupIndex switch
        {
            0 => new Vector3(2550f, 4.5f, 2450f),
            1 => new Vector3(2860f, 4.5f, 3550f),
            2 => new Vector3(2250f, 4.5f, 2260f),
            3 => new Vector3(2050f, 4.5f, 2160f),
            _ => new Vector3(2550f, 4.5f, 2450f)
        };
        float newSize = _ustyeSetupIndex switch
        {
            0 => 220f,
            1 => 200f,
            2 => 180f,
            3 => 160f,
            _ => 220f
        };
        _cameraRig.SetDefaultView(newFocus, newSize);
        _cameraRig.ResetToDefault();
        string name = _ustyeSetupIndex switch
        {
            0 => "Central Bridge (CROSS-C)",
            1 => "Southern Crossing (CROSS-S)",
            2 => "City Maneuver (Panel Blocks)",
            3 => "Reconnaissance & LOS Occlusion",
            _ => "Default"
        };
        _status.Text = $"Ustye setup {_ustyeSetupIndex + 1}/4: {name}";
        _orderNotification?.ShowNotification($"Setup {_ustyeSetupIndex + 1}: {name}");
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

    public static string FormatTitle(int assignedSide, string scenario)
    {
        string name = scenario switch
        {
            "visibility" => "RECON SCENARIO",
            "combat" => "COMBAT SCENARIO",
            "recon-raid" => "RECON RAID MISSION",
            "battle" => "MEETING ENGAGEMENT",
            "bridge-crossing" => "BRIDGE CROSSING",
            "vehicle-traffic" => "VEHICLE TRAFFIC PLAYGROUND",
            "group-move" => "GROUP MOVE PLAYGROUND",
            "ustye-ground" => "USTYE PLAYGROUND",
            "ustye-raid" => "USTYE RECON RAID",
            "forest-visibility" => "FOREST VISIBILITY POLYGON",
            "infantry-playground" => "INFANTRY PLAYGROUND",
            _ => "M0 NETWORK TEST"
        };
        return $"DEAD GROUND   /   SIDE {assignedSide}   /   {name}";
    }

    internal void ApplyPlayerHomeView(int assignedPlayerId)
    {
        Vector3 homeFocus = GetDefaultHomeFocus(_scenario, assignedPlayerId, _initialSpawnFocus, _hasExplicitCameraFocus);
        _cameraRig.SetDefaultView(homeFocus, _initialSpawnSize);

        bool shouldCenter = ShouldCenterOnHomeView(
            _cameraRig.Focus, _cameraRig.Size, _cameraRig.TargetSize, _cameraRig.ViewYaw, _cameraRig.ViewPitch, _initialSpawnFocus, _initialSpawnSize);
        if (shouldCenter)
        {
            _cameraRig.CenterOn(homeFocus);
            _initialSpawnFocus = homeFocus;
        }

        GD.Print($"CAMERA_HOME side={assignedPlayerId} focus={_cameraRig.Focus} default_focus={homeFocus} centered={shouldCenter}");
    }

    internal static bool ShouldCenterOnHomeView(Vector3 currentFocus, float currentSize, float targetSize, float viewYaw, float viewPitch, Vector3 spawnFocus, float spawnSize)
    {
        Vector3 focusDelta = currentFocus - spawnFocus;
        bool cameraMoved = (focusDelta.X * focusDelta.X + focusDelta.Z * focusDelta.Z) > 1.0f
            || MathF.Abs(currentSize - spawnSize) > 0.5f
            || MathF.Abs(targetSize - spawnSize) > 0.5f
            || MathF.Abs(viewYaw) > 0.01f
            || MathF.Abs(viewPitch - OrbitCameraPose.DefaultPitch) > 0.01f;
        return !cameraMoved;
    }

    internal static Vector3 GetDefaultHomeFocus(string scenario, int assignedPlayerId, Vector3 fallbackFocus, bool hasExplicitFocus)
    {
        if (hasExplicitFocus)
        {
            return fallbackFocus;
        }

        if (scenario == "battle")
        {
            return assignedPlayerId switch
            {
                1 => new Vector3(BattleScenario.SovietStart.X, 0f, BattleScenario.SovietStart.Y),
                2 => new Vector3(BattleScenario.UsStart.X, 0f, BattleScenario.UsStart.Y),
                _ => fallbackFocus
            };
        }

        if (scenario == "forest-visibility")
        {
            return new Vector3(100f, 0f, 110f);
        }

        if (scenario == "infantry-playground")
        {
            return new Vector3(130f, 0f, 180f);
        }

        return fallbackFocus;
    }

    internal static string FormatControlsHelpText(bool isMacOs) =>
        isMacOs
            ? "  LMB click/box  /  2xLMB type  /  Cmd/Ctrl+1-9 set  /  Shift+1-9 add  /  1-9 select (twice: center)  /  Z/X/C stance  /  RMB order  /  WASD pan"
            : "  LMB click/box  /  2xLMB type  /  Ctrl/Cmd+1-9 set  /  Shift+1-9 add  /  1-9 select (twice: center)  /  Z/X/C stance  /  RMB order  /  WASD pan";

    private void SetupMinimapDemo()
    {
        ReadOnlySpan<OwnUnitObservation> ownUnits = _session.Replica.OwnUnits;
        Span<ContactObservation> demoContacts = stackalloc ContactObservation[2]
        {
            new(new ContactId(1), ContactState.LastKnown, ContactClassification.Tank, 0, new NumericsVector2(3100f, 2550f), LastSeenTick: 10),
            new(new ContactId(2), ContactState.Current, ContactClassification.Recon, 1, new NumericsVector2(2860f, 2220f), LastSeenTick: 30)
        };

        byte[] buf = new byte[8192];
        int len = WireProtocol.WriteSnapshot(
            buf,
            sequence: 30,
            tick: 30,
            units: ownUnits,
            contacts: demoContacts,
            events: [],
            mission: default);

        _session.Replica.TryApply(buf.AsSpan(0, len));

        if (ownUnits.Length > 0)
        {
            for (int i = 0; i < Math.Min(3, ownUnits.Length); i++)
            {
                _unitViews.Selected.Add(ownUnits[i].Id);
            }
        }

        _cameraRig.Model.CurrentSize = 520f;
        _cameraRig.Model.SetZoom(520f);
        _cameraRig.CenterOn(new Vector3(2650f, 4.5f, 2450f));
        UpdateViews();
        _minimap?.UpdateMinimap(0.016);
    }

    public string TitleText => _title?.Text ?? string.Empty;

    public override void _ExitTree()
    {
        _combatEffects?.Clear();
        _fogOfWar?.Dispose();
        _missionPanel?.Cleanup();
        _session?.Dispose();
    }
}
